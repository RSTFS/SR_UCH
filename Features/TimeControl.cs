// 时间控制：梦境（本地定格）/ 冻结角色（只冻自己）。
//
// 「时停」（联机定格）已整体移除。原因（逐行核对反编译源码后的硬结论）：
//   不存在任何游戏原生网络消息能让没装 mod 的房客把 Time.timeScale 写成 0 ——
//     - Modifiers.GameSpeedValues = { 1, 0.5, 0.75, 1.5, 2 }，没有 0 档            :138933
//     - MsgApplyRuleset 只同步 rulesetXML；Modifiers 从 XML 只解析各 Mode 索引、
//       不同步数组值（:140185）；GameSpeed getter 永远返回数组元素            :139134
//     - SoftPauseEvent 是本地事件，只 Character.Pause()（animator / velocity）  :61174 / :145436
//     - PiecePlacementCursor 的 [SyncVar] paused 只管放置光标                 :66640 / :66959
//   上一版的「双轨降级」（房主发真实速度规则让未装端进最慢档、已装端压到 0）
//   只是"有人慢下来、有人定住"的混合语义，名不副实，因此不再保留。
//   联机需要真正的定格时，应要求全员安装并由各端自行落位；本模块不再提供。
//
// 设计要点（与历轮审查结论对齐）：
//  1) 状态唯一来源 = 配置项。UI / 快捷键都只改配置值，由同一个 Tick 统一落位，
//     避免"同一修复两条路径只做一条"。
//  2) 零 Harmony 补丁、零反射登记、零网络消息；自检基线 patches/reflection/api 不变。
//  3) 每帧覆盖 Time.timeScale = 0；游戏在 OnGameStart / 规则消息 / 换场景会写回。
//     SR 全仓 0 处 Time.deltaTime，定格期间面板 / 动画 / 锁自愈照常工作。
//  4) 梦境只在离线 / 本地派对生效；联机对局里自动关闭开关，避免只冻自己造成误会。

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.timecontrol", "TimeControl", "1.2", Provides = new string[] { "sr.brief.dream" })]
    public class TimeControl : ITweak {
        private const string Sec = "时间控制";

        private static ConfigEntry<bool> _dream, _freeze;
        private static ConfigEntry<KeyCode> _dreamKey, _freezeKey;

        private static bool _applied;            // 当前是否已由本功能接管 timeScale
        private static float _savedScale = 1f;  // 定格前的真实时间缩放（还原的权威值）
        private static Character _frozenChar;

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg(plugin);
                // 运行时状态绝不跨进程残留：上次退出时若停在定格里，重开会被 0 倍速卡住。
                _dream.Value = false;
                _freeze.Value = false;
                _applied = false;
                _savedScale = 1f;
                SRApi.Ctl.MarkConfigDirty(plugin.Config);

                SRApi.RegisterKey("时间控制-梦境", _dreamKey, "toggle");
                SRApi.RegisterKey("时间控制-冻结角色", _freezeKey, "toggle");
                SRApi.RegisterTick("时间控制-快捷键", TickKeys);
                SRApi.RegisterTick("时间控制-落位", Tick);
                SRApi.RegisterSceneHook(OnSceneChanged);

                // 局内简报的状态出口：只导出「已本地化的展示文本」，不导出内部类型。
                Slots.Provide("sr.brief.dream", (Func<string>)(() => BriefState()));
            } catch (Exception __ex) {
                SRApi.Guard.Log("时间控制/初始化", __ex);
            }
        }

        // ITweak 当前没有 Shutdown 契约，这个方法不会被调用；留对称入口供契约补齐时直接使用。
        public void Shutdown() {
            Restore();
            ReleaseFreeze();
        }

        private static void SelfReg(IFeatureHost plugin) {
            SRApi.LocSec(Sec, "时间控制", "Time Control");
            SRApi.Nav(Sec, 91);   // 联机(90) 之后，避开与 Online 的 rank 90 撞号
            SRApi.RegisterPage(Sec, Render);

            _dream = plugin.Config.Bind(Sec, "Dream", false,
                "梦境：把游戏时间设为 0，本地画面与物理全部定格。仅离线 / 本地派对生效。");
            _dreamKey = plugin.Config.Bind(Sec, "Dream Key", KeyCode.None, "梦境的快捷键。");
            _freeze = plugin.Config.Bind(Sec, "Freeze Self", false,
                "冻结角色：只冻住自己的角色，其他玩家照常移动。与 EX 同名开关不要同时打开。");
            _freezeKey = plugin.Config.Bind(Sec, "Freeze Self Key", KeyCode.None, "冻结角色的快捷键。");

            SRApi.LocKey(Sec, "Dream", "梦境", "Dream");
            SRApi.LocDesc(Sec, "Dream",
                "梦境\n本地定格：把游戏时间设为 0\n画面、物理、危险物全部静止，面板与快捷键仍可操作\n只在离线 / 本地派对生效；联机时会自动关闭（没有联机定格功能）",
                "Dream\nlocal freeze: sets the game clock to 0\nnothing moves; the panel and hotkeys still work\noffline / local party only; auto-disabled online (no online freeze available)");
            SRApi.LocKey(Sec, "Dream Key", "梦境快捷键", "Dream key");
            SRApi.LocKey(Sec, "Freeze Self", "冻结角色", "Freeze self");
            SRApi.LocDesc(Sec, "Freeze Self",
                "冻结角色\n只冻住自己的角色（速度清零、动画定格，网络同步给其他人）\n其他玩家照常移动\n与 EX 模块的同名开关不要同时打开",
                "Freeze self\nfreezes only your character (velocity cleared, animation paused, synced to others)\neveryone else keeps moving\ndo not enable this together with the EX module's switch of the same name");
            SRApi.LocKey(Sec, "Freeze Self Key", "冻结角色快捷键", "Freeze self key");
            SRApi.LocDesc(Sec, "Dream Key",
                "梦境快捷键\n按下切换本地定格\n可改绑，支持组合键；联机时按下只提示不可用",
                "Dream key\ntoggles the local freeze\nrebindable, combos supported; online it only shows a hint");
            SRApi.LocDesc(Sec, "Freeze Self Key",
                "冻结角色快捷键\n按下切换只冻自己的角色\n可改绑，支持组合键",
                "Freeze self key\ntoggles freezing only your own character\nrebindable, combos supported");
        }

        /// <summary>给局内简报读的状态摘要；没有生效项时返回 null（浮层自动隐藏那一行）。</summary>
        public static string BriefState() {
            if (_applied) return SRApi.T("梦境 · 本地定格", "Dream · local freeze");
            if (Val(_freeze)) return SRApi.T("冻结角色 · 只冻自己", "Freeze self · only you");
            return null;
        }

        // ================= 页面 =================
        private static void Render() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("梦境", "Dream") + " -",
                    SRApi.T("本地定格 · 离线 / 本地派对", "Local freeze · offline / local party"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    string k = e.Definition.Key;
                    return k == "Dream" || k == "Dream Key";
                });
                GUILayout.Space(SRApi.Ctl.Sc(6));

                SRApi.Ctl.SectionTitle("- " + SRApi.T("冻结角色", "Freeze self") + " -",
                    SRApi.T("只冻自己 · 与 EX 二选一", "Only you · do not combine with EX"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    string k = e.Definition.Key;
                    return k == "Freeze Self" || k == "Freeze Self Key";
                });
            } catch (Exception __ex) {
                SRApi.Guard.Log("时间控制/渲染", __ex);
            }
        }

        // ================= 快捷键 =================
        private static void TickKeys() {
            try {
                if (!SRApi.GateMaster || SRApi.Typing) return;
                if (SRApi.ComboKeyDown(_dreamKey)) Toggle(_dream);
                if (SRApi.ComboKeyDown(_freezeKey)) Toggle(_freeze);
            } catch (Exception __ex) {
                SRApi.Guard.Log("时间控制/快捷键", __ex);
            }
        }

        private static void Toggle(ConfigEntry<bool> e) {
            if (e == null) return;
            e.Value = !e.Value;
            SRApi.Ctl.MarkConfigDirty(e.ConfigFile);
        }

        // ================= 单一落位 Tick =================
        private static void Tick() {
            try {
                // 总开关关闭：立刻交还时间，不修改配置值（再打开时原状态仍在）。
                if (!SRApi.GateMaster) {
                    Restore();
                    ReleaseFreeze();
                    return;
                }

                bool dream = Val(_dream);
                bool freeze = Val(_freeze);

                // --- 梦境：只允许本地 ---
                if (dream && InOnlineGame()) {
                    _dream.Value = false;
                    dream = false;
                    SRApi.Notify(SRApi.T("梦境仅离线 / 本地派对可用；联机定格功能已移除",
                                         "Dream is offline / local party only; online freeze has been removed"));
                }

                // --- 落位：每帧覆盖，抵消游戏在 OnGameStart / 规则消息 / 换场景的写回 ---
                if (dream) ApplyZero();
                else Restore();

                // --- 冻结角色 ---
                ApplyFreeze(freeze);
            } catch (Exception __ex) {
                SRApi.Guard.Log("时间控制/落位", __ex);
                // 异常路径也必须把时间交还，绝不能把玩家卡在 0 倍速里。
                Restore();
            }
        }

        private static bool Val(ConfigEntry<bool> e) { return e != null && e.Value; }

        private static bool InOnlineGame() {
            try {
                LobbyManager lm = LobbyManager.instance;
                return lm != null && lm.IsInOnlineGame;
            } catch (Exception __ex) {
                SRApi.Guard.Log("时间控制/联机判定", __ex);
                return false;
            }
        }

        private static void ApplyZero() {
            if (!_applied) {
                float cur = Time.timeScale;
                if (cur <= 0f || float.IsNaN(cur)) {
                    try {
                        Modifiers m = Modifiers.GetInstance();
                        if (m != null) cur = m.GameSpeed;
                    } catch (Exception __ex) {
                        SRApi.Guard.Log("时间控制/读规则速度", __ex);
                    }
                }
                _savedScale = (cur > 0f && !float.IsNaN(cur)) ? cur : 1f;
                _applied = true;
            }
            Time.timeScale = 0f;
        }

        private static void Restore() {
            if (!_applied) return;
            _applied = false;
            float target = (_savedScale > 0f && !float.IsNaN(_savedScale)) ? _savedScale : 1f;
            Time.timeScale = target;
            _savedScale = 1f;
        }

        // ================= 冻结角色 =================
        private static void ApplyFreeze(bool want) {
            Character c = null;
            if (want) {
                try {
                    Character[] all = SRApi.FrameObjects<Character>();
                    for (int i = 0; i < all.Length; i++) {
                        Character x = all[i];
                        if (x != null && x.hasAuthority && !x.Success) { c = x; break; }
                    }
                } catch (Exception __ex) {
                    SRApi.Guard.Log("时间控制/找本地角色", __ex);
                }
            }
            if (_frozenChar != null && (c == null || c != _frozenChar)) {
                try { _frozenChar.Unfreeze(); } catch (Exception __ex) { SRApi.Guard.Log("时间控制/解冻", __ex); }
                _frozenChar = null;
            }
            if (want && c != null && c != _frozenChar) {
                try {
                    c.Freeze(false, true, false);
                    _frozenChar = c;
                } catch (Exception __ex) {
                    SRApi.Guard.Log("时间控制/冻结", __ex);
                }
            }
        }

        private static void ReleaseFreeze() {
            if (_frozenChar == null) return;
            try { _frozenChar.Unfreeze(); } catch (Exception __ex) { SRApi.Guard.Log("时间控制/解冻", __ex); }
            _frozenChar = null;
        }

        private static void OnSceneChanged() {
            // 换场景时游戏会写回 timeScale；重置落位标记让下一帧重新捕获规则速度。
            _applied = false;
            _savedScale = 1f;
            ReleaseFreeze();
        }
    }
}
