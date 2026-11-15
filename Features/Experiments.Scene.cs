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
            GUILayout.Space(SRApi.Ctl.Sc(6));
            SRApi.Ctl.SectionTitle(SRApi.T("树屋", "Treehouse"), SRApi.T("在树屋大厅里直接开始关卡.",
                       "Start the level immediately from the treehouse."));
            SRApi.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
                return e.Definition.Key.StartsWith("Lobby ");
            });
                if (LobbyTools.Note != null && LobbyTools.Note.Length > 0) {
                    GUILayout.Space(SRApi.Ctl.Sc(2));
                    DrawStatus(LobbyTools.Note);
                }

                GUILayout.Space(SRApi.Ctl.Sc(6));
                SRApi.Ctl.SectionTitle(SRApi.T("加载与切场景", "Loading and scene switch"), SRApi.T("跳过加载动画. 温柔加载在切换场景时先卸载再回收.",
                         "Skip the loading animation. Gentle load unloads and collects before switching."));
                SRApi.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
                    return e.Definition.Key.StartsWith("Splash ") || e.Definition.Key == "Gentle Load";
                });
                Action doCloseMask = delegate {
                    SplashTools.CloseMaskNow();
                    SRApi.Notify(SplashTools.Note ?? SRApi.T("关闭遮罩：已执行一次", "Close the mask: executed once"));
                };
                if (SRApi.Ctl.HotkeyButton("exp.close_mask", SRApi.T("关闭遮罩", "Close the mask"),
                        SRApi.T("立刻关掉当前加载遮罩\n只关这一次，不改变「跳过加载动画」设置",
                            "dismiss the current loading mask\none shot; does not change the skip-load-animation setting"),
                        doCloseMask)) doCloseMask();

                GUILayout.Space(SRApi.Ctl.Sc(6));
            } catch (Exception __ex) { SRApi.Guard.Log("场景页 渲染", __ex); }
        }

        
}


    // 加载遮罩与温柔加载；SRHost 为协程挂载点（SR 核心非 MonoBehaviour）。
    internal sealed class SRHost : MonoBehaviour { }

    [SrFeature("sr.splashtools", "SplashTools", "1.0")]

    public class SplashTools : ITweak {
        private static ConfigEntry<bool> _skipLoad;
        private static ConfigEntry<bool> _gentleLoad;
        private static ConfigEntry<bool> _closeLoadUi;   // 关闭遮罩时是否顺带关掉加载界面


        internal static string Note = "";

        private static void SelfReg() {
            // Scene 段条目已渲染在场景页，须 NavHide，否则兜底会重复建栏目。
            SRApi.NavHide("Scene");
            // SkipBool 为 protected，登记后游戏改名时自检能报出来。
            SRApi.Ref.Override("UISplashScreen.SkipBool", "关闭跳过加载动画需清此标志");
            SRApi.LocKey("Scene", "Splash Skip Load", "跳过加载动画", "Skip load animation");
            SRApi.LocDesc("Scene", "Splash Skip Load", "跳过加载动画\n加载画面一出现就跳过\n无副作用，只省等待", "Skip loading animation\nskips the splash as soon as it appears\nno side effects, only saves the wait");
            SRApi.LocKey("Scene", "Close Load UI", "同时关闭加载界面", "Also close loading screen");
            SRApi.LocDesc("Scene", "Close Load UI",
                "同时关闭加载界面\n点「关闭遮罩」时把平板加载界面一起隐藏\n注意：加载并不会更快，界面消失后可能看起来像卡住",
                "Also close loading screen\nhides the tablet loading screen when the mask is closed\nnote: loading is not faster, the game may look frozen without it");
            SRApi.LocKey("Scene", "Gentle Load", "温柔加载", "Gentle load");
            SRApi.LocDesc("Scene", "Gentle Load", "温和加载\n切换场景时走卸载加 GC 流程，减轻卡顿\n切到主菜单时交给游戏自身流程", "Gentle load\nunload plus GC when switching scenes to reduce stutter\nmain-menu transitions use the game's own path");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _skipLoad = plugin.Config.Bind("Scene", "Splash Skip Load", false, "加载画面一出现就立刻跳过。");
                _closeLoadUi = plugin.Config.Bind("Scene", "Close Load UI", false,
                    "关闭遮罩时把平板加载界面一起隐藏（默认关：加载本身不会变快）。");
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
                ApplySkip();
            } catch (Exception __ex) { SRApi.Guard.Log("加载遮罩 Tick", __ex); }
        }

        // Skip() 只置真 SkipBool 且无取消路径；关闭时反射写回 false 才能恢复按时间推进。
        private static System.Reflection.FieldInfo _fiSkip;
        private static bool _fiSkipTried;
        private static void ApplySkip() {
            try {
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

        // 清掉 SkipBool（protected，无公开关闭接口）。
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

        // 游戏用 while (State != SHOW) 等遮罩：若对象被停用过（含旧版本遗留），加载会永久卡住。
        // 这里在拿到遮罩时确保它是 active 的。
        internal static void EnsureSplashActive(UISplashScreen sp) {
            try {
                if (sp != null && sp.gameObject != null && !sp.gameObject.activeSelf) sp.gameObject.SetActive(true);
            } catch (Exception __es) { SRApi.Guard.Log("关闭遮罩/恢复激活", __es); }
        }

        // Instance 为空时才回退全场景扫描，回退路径 15 帧节流。
        private static int _splashScanAt = -9999;
        private static LoadingInterstitialSplash FindSplash() {
            try { LoadingInterstitialSplash inst = LoadingInterstitialSplash.Instance; if (inst != null) return inst; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            if (Time.frameCount - _splashScanAt < 15) return null;
            _splashScanAt = Time.frameCount;
            try { return UnityEngine.Object.FindObjectOfType<LoadingInterstitialSplash>(); } catch { return null; }
        }

        // 一次性关闭当前遮罩，不改变「跳过加载动画」设置。
        internal static void CloseMaskNow() {
            try {
                LoadingInterstitialSplash sp = FindSplash();
                EnsureSplashActive(sp);
                if (sp == null) {
                    Note = SRApi.T("当前没有遮罩", "No mask on screen");
                    SRApi.NotifyT("当前没有遮罩", "No mask on screen");
                    return;
                }
                bool closed = false;
                try { sp.Skip(); closed = true; }
                catch (Exception __sk) { SRApi.Guard.Log("关闭遮罩/Skip", __sk); }
                if (!closed) {
                    try { sp.FadeOut(); closed = true; } catch (Exception __fo) { SRApi.Guard.Log("关闭遮罩/FadeOut", __fo); }
                }
                // Skip 会置真 SkipBool 且无取消接口：此处立刻清回，保证只关这一次。
                try { ClearSkip(sp); } catch (Exception __cs) { SRApi.Guard.Log("关闭遮罩/清SkipBool", __cs); }
                if (_closeLoadUi != null && _closeLoadUi.Value) {
                    CloseLoadingUi();
                    HideSplashVisuals(sp);
                }
                Note = closed ? SRApi.T("已关闭一次遮罩", "Mask closed once")
                              : SRApi.T("遮罩不在可关闭状态", "Mask is not in a closable state");
                SRApi.LogInfo("[实验.场景] " + Note);
            } catch (Exception __ex) { SRApi.Guard.Log("关闭遮罩", __ex); }
        }

        // 隐藏加载动画的"画面"但**保留对象与状态机**：
        // 游戏用 `while (State != SHOW) yield return null` 等遮罩；一旦 SetActive(false)，
        // 该对象的 Update 不再执行、State 永远到不了 SHOW → 返回主界面/加载协程永久卡住。
        // 所以只把可见元素透明化（CanvasGroup/Images/Texts 均为 public 字段）。
        internal static void HideSplashVisuals(UISplashScreen sp) {
            CanvasSplash cs = sp as CanvasSplash;
            if (cs == null) return;
            try {
                if (cs.CanvasGroup != null) cs.CanvasGroup.alpha = 0f;
                if (cs.Images != null) for (int i = 0; i < cs.Images.Count; i++) if (cs.Images[i] != null) { Color c = cs.Images[i].color; c.a = 0f; cs.Images[i].color = c; }
                if (cs.Texts != null) for (int i = 0; i < cs.Texts.Count; i++) if (cs.Texts[i] != null) { Color c = cs.Texts[i].color; c.a = 0f; cs.Texts[i].color = c; }
            } catch (Exception __hv) { SRApi.Guard.Log("关闭遮罩/隐藏动画画面", __hv); }
        }

        // 顺带隐藏平板加载界面；不会加快加载，可能看起来像卡住。
        private static void CloseLoadingUi() {
            try {
                TabletLoadingScreen ui = UnityEngine.Object.FindObjectOfType<TabletLoadingScreen>();
                if (ui == null) return;
                try { ui.gameObject.SetActive(false); } catch (Exception __ss) { SRApi.Guard.Log("关闭遮罩/隐藏加载界面", __ss); }
                SRApi.LogInfo("[实验.场景] 已隐藏平板加载界面");
            } catch (Exception __ex) { SRApi.Guard.Log("关闭遮罩/查找加载界面", __ex); }
        }

        internal static bool GentleOn() { return _gentleLoad != null && _gentleLoad.Value && SRApi.GateMaster; }
    }

    // 温柔加载：前置 SceneManagerWrapper 两个入口，改走 DoGentleSceneLoad。
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

        // Prefix 返回 false = 抑制原版；Start true = 已接管，false = 未接管须放行。
        [HarmonyPatch(typeof(SceneManagerWrapper), "LoadScene")]
        [HarmonyPrefix]
        static bool PreLoadScene(string sceneName) {
            try {
                        if (!Should(sceneName)) return true;
                        return !Start(sceneName);
            } catch (Exception __ex) { SRApi.Guard.Log("温和加载(同步)", __ex); return true; }
        }

        [HarmonyPatch(typeof(SceneManagerWrapper), "LoadSceneAsync")]
        [HarmonyPrefix]
        static bool PreLoadSceneAsync(string sceneName) {
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
                    SRApi.LogWarn("[场景] 温柔加载已在进行中，本次请求回落原版（" + sceneName + "）");
                    return false;
                }
                _target = sceneName;
                _host.StartCoroutine(SceneManagerWrapper.DoGentleSceneLoad(sceneName));
                // 协程无 isDone：场景切到目标即算完成，5 秒后自动解锁。
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
                    else if (SRApi.Env.SceneName == _target) _runningUntil = -1f;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }
    }
    [SrFeature("sr.lobbytools", "LobbyTools", "1.0")]
    public class LobbyTools : ITweak {
        private static ConfigEntry<KeyCode> _launchKey;

        internal static string Note = "";

        private static void SelfReg() {
            SRApi.LocKey("Scene", "Lobby Launch Key", "立即开始键", "Launch now key");
            SRApi.LocDesc("Scene", "Lobby Launch Key", "立即开始关卡\n优先开始你投过票的那扇门，没投票时选票数最高的门\n仅房主或离线有效；没绑按键时不生效", "Launch the level now\nprefers the portal you voted for, otherwise the most-voted portal\nhost or offline only; does nothing while unbound");

        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _launchKey = plugin.Config.Bind("Scene", "Lobby Launch Key", KeyCode.None, "按此键立即开始关卡（None = 不绑）。优先你投过票的门，否则票数最高的门；仅房主或离线有效。");
                SRApi.RegisterKey("实验-树屋立即开始", _launchKey, "press", delegate {
                    try {
                        if (!SRApi.GateMaster || !SRApi.Env.InTreehouse) return;
                        LevelSelectController __lsc = Lsc();
                        if (__lsc == null) return;
                        DoLaunch(__lsc);
                    } catch (Exception __lk) { SRApi.Guard.Log("立即开始", __lk); }
                });
                SRApi.RegisterSceneHook(Reset);
            } catch (Exception __ex) { SRApi.Guard.Log("树屋工具 初始化", __ex); }
        }

        private static void Reset() { Note = ""; }

        private static LevelSelectController Lsc() {
            try { return LobbyManager.instance.CurrentLevelSelectController; } catch { return null; }
        }

        private static void DoLaunch(LevelSelectController lsc) {
            if (!SRApi.CanControlLocally) { SRApi.NotifyT("仅房主可开始关卡", "Host only"); return; }
            LevelPortal[] portals = lsc.portals;
            if (portals == null || portals.Length == 0) { SRApi.NotifyT("未找到可用关卡门", "No level portal available"); return; }
            Character hostChar = null;
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    foreach (LobbyPlayer lp in lm.GetLobbyPlayers()) {
                        if (lp == null || !lp.isLocalPlayer) continue;
                        Character lc = lp.CharacterInstance;
                        if (lc != null) { hostChar = lc; break; }
                    }
                }
            } catch (Exception __hw) { SRApi.Guard.Log("立即开始/找本地角色", __hw); }
            if (hostChar == null) {
                try {
                    Character[] chars = SRApi.FrameObjects<Character>();
                    for (int i = 0; i < chars.Length; i++) {
                        Character c = chars[i];
                        if (c != null && c.hasAuthority) { hostChar = c; break; }
                    }
                } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
            LevelPortal pick = null;
            if (hostChar != null) {
                for (int i = 0; i < portals.Length; i++) {
                    LevelPortal p = portals[i];
                    if (p == null || p.Votes == null) continue;
                    if (p.Votes.ContainsKey(hostChar)) { pick = p; break; }
                }
            }
            if (pick == null) {
                int best = 0;
                for (int i = 0; i < portals.Length; i++) {
                    LevelPortal p = portals[i];
                    if (p == null || p.Votes == null) continue;
                    if (p.Votes.Count > best) { best = p.Votes.Count; pick = p; }
                }
                if (pick != null) SRApi.LogInfo("[立即开始] 没找到本机角色的投票，改用票数最高的门（" + best + " 票）");
            }
            if (pick == null) {
                SRApi.NotifyT("未选择关卡", "No level selected");
                return;
            }
            lsc.LaunchLevel(pick);
            SRApi.NotifyT("关卡已开始", "Level started");
        }










    }
}
