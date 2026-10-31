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
            SRApi.Ctl.SectionTitle("- " + SRApi.T("树屋", "Treehouse") + " -", SRApi.T("在树屋大厅里直接开始关卡.",
                       "Start the level immediately from the treehouse."));
            SRApi.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
                return e.Definition.Key.StartsWith("Lobby ");
            });
                if (LobbyTools.Note != null && LobbyTools.Note.Length > 0) {
                    GUILayout.Space(SRApi.Ctl.Sc(2));
                    DrawStatus(LobbyTools.Note);
                }

                // 加载与切场景
                GUILayout.Space(SRApi.Ctl.Sc(6));
                SRApi.Ctl.SectionTitle("- " + SRApi.T("加载与切场景", "Loading and scene switch") + " -", SRApi.T("跳过加载动画. 温柔加载在切换场景时先卸载再回收.",
                         "Skip the loading animation. Gentle load unloads and collects before switching."));
                SRApi.RenderSectionEntries("Scene", delegate (ConfigEntryBase e) {
                    return e.Definition.Key.StartsWith("Splash ") || e.Definition.Key == "Gentle Load";
                });
                // 关闭遮罩：同样按返回值执行。
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
            SRApi.LocDesc("Scene", "Splash Skip Load", "跳过加载动画\n加载画面一出现就跳过\n无副作用，只省等待", "Skip loading animation\nskips the splash as soon as it appears\nno side effects, only saves the wait");
            SRApi.LocKey("Scene", "Gentle Load", "温柔加载", "Gentle load");
            SRApi.LocDesc("Scene", "Gentle Load", "温和加载\n切换场景时走卸载加 GC 流程，减轻卡顿\n切到主菜单时交给游戏自身流程", "Gentle load\nunload plus GC when switching scenes to reduce stutter\nmain-menu transitions use the game's own path");
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
            try { LoadingInterstitialSplash inst = LoadingInterstitialSplash.Instance; if (inst != null) return inst; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            if (Time.frameCount - _splashScanAt < 15) return null;
            _splashScanAt = Time.frameCount;
            try { return UnityEngine.Object.FindObjectOfType<LoadingInterstitialSplash>(); } catch { return null; }
        }

        // 一次性关闭当前加载遮罩：按钮点一次走一次，不改变「跳过加载动画」的设置。
        internal static void CloseMaskNow() {
            try {
                LoadingInterstitialSplash sp = FindSplash();
                if (sp == null) {
                    Note = SRApi.T("当前没有遮罩", "No mask on screen");
                    SRApi.Notify(SRApi.T("当前没有遮罩", "No mask on screen"));
                    return;
                }
                // 无条件尝试 Skip：以前只在 FADING_IN/SHOW 两个状态才调，其余状态直接"没反应"。
                bool closed = false;
                try { sp.Skip(); closed = true; }
                catch (Exception __sk) { SRApi.Guard.Log("关闭遮罩/Skip", __sk); }
                if (!closed) {
                    try { sp.FadeOut(); closed = true; } catch (Exception __fo) { SRApi.Guard.Log("关闭遮罩/FadeOut", __fo); }
                }
                // Skip 会把游戏的 SkipBool 置真且没有取消入口：这里立刻清回 false，
                // 保证"只关这一次"，下一次加载照常走（与「跳过加载动画」开关互不影响）。
                try { ClearSkip(sp); } catch (Exception __cs) { SRApi.Guard.Log("关闭遮罩/清SkipBool", __cs); }
                Note = closed ? SRApi.T("已关闭一次遮罩", "Mask closed once")
                              : SRApi.T("遮罩不在可关闭状态", "Mask is not in a closable state");
                SRApi.LogInfo("[实验.场景] " + Note);
            } catch (Exception __ex) { SRApi.Guard.Log("关闭遮罩", __ex); }
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
                    else if (SRApi.Env.SceneName == _target) _runningUntil = -1f;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }
    }
    // 树屋：立即开始。
    [SrFeature("sr.lobbytools", "LobbyTools", "1.0")]
    public class LobbyTools : ITweak {
        private static ConfigEntry<KeyCode> _launchKey;

        internal static string Note = "";

        private static void SelfReg() {
            SRApi.LocKey("Scene", "Lobby Launch Key", "立即开始键", "Launch now key");
            SRApi.LocDesc("Scene", "Lobby Launch Key", "立即开始关卡\n按下立刻开始，自动选票数最高的门\n需要房主权限；未绑定时不生效", "Launch now\nstarts at once, picking the most-voted door\nhost only; no effect while unbound");

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
                if (!SRApi.Env.InTreehouse) return;
                LevelSelectController lsc = Lsc();
                if (lsc == null) return;
                if (_launchKey != null && _launchKey.Value != KeyCode.None && SRApi.ComboKeyDown(_launchKey)) {
                    try { DoLaunch(lsc); } catch (Exception __ex) { SRApi.Guard.Log("立即开始", __ex); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("树屋工具 Tick", __ex); }
        }

        private static void DoLaunch(LevelSelectController lsc) {
            // 判 SRApi.CanControlLocally（离线时为真）；LaunchLevel 是纯本地调用，与广播能力无关。
            if (!SRApi.CanControlLocally) { SRApi.Notify(SRApi.T("仅房主可开始关卡", "Host only")); return; }
            LevelPortal[] portals = lsc.portals;
            if (portals == null || portals.Length == 0) { SRApi.Notify(SRApi.T("未找到可用关卡门", "No level portal available")); return; }
            // 1) 优先"房主自己投过票的那扇门"：先按本地玩家拿角色，再退化到任意 hasAuthority 角色。
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
            // 2) 兜底（与提示文案一致）：自动选票数最高的那扇门；全都没票才报"未选择关卡"。
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
                SRApi.Notify(SRApi.T("未选择关卡", "No level selected"));
                return;
            }
            lsc.LaunchLevel(pick);
            SRApi.Notify(SRApi.T("关卡已开始", "Level started"));
        }










    }
}
