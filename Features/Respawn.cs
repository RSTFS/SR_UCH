// 重生相关：自动重生延迟、出生无敌、炮弹卡死自动重生、挑战死亡不弹记分板。
// 只影响本地角色；自动重生的挑战路径由 QuickAdjust 的自动重试发送，这里提供"延迟"判定。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.respawn", "Respawn", "1.0")]
    public class Respawn : ITweak {
        private const string Sec = "Quick Adjust";
        private static ConfigEntry<float> _delay;          // 重生延迟（0-3 秒）
        private static ConfigEntry<float> _invincible;     // 出生无敌（0-10 秒）
        private static ConfigEntry<bool> _autoRespawn;     // 自动重生（自由模式；挑战由自动重试负责）
        private static ConfigEntry<bool> _cannonRespawn;   // 炮弹卡死自动重生
        private static ConfigEntry<bool> _noScoreboard;    // 挑战死亡不弹记分板
        private static readonly Dictionary<int, float> _deadSince = new Dictionary<int, float>();
        private static float _cannonStuckAt = -1f;

        // QuickAdjust 的自动重试用来判断"还要等多久"。
        internal static float DelaySeconds { get { try { return _delay != null ? Mathf.Clamp(_delay.Value, 0f, 3f) : 0f; } catch { return 0f; } } }

        private static bool ModeOk() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return false;
                return gs.GameMode == GameState.GameMode.CHALLENGE || gs.GameMode == GameState.GameMode.FREEPLAY;
            } catch (Exception __ex) { SRApi.Guard.Log("重生/模式判定", __ex); return false; }
        }

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Respawn Delay", "重生延迟", null);
            SRApi.LocDesc(Sec, "Respawn Delay", "重生延迟\n死亡后等多少秒再自动重生\n挑战/自由模式；0 = 立即", "Respawn delay\nseconds to wait after death before respawning automatically\nchallenge and freeplay; 0 = instant");
            SRApi.LocKey(Sec, "Spawn Invincible", "出生无敌", null);
            SRApi.LocDesc(Sec, "Spawn Invincible", "出生无敌\n每次出生后无敌多少秒\n挑战/自由模式；0 = 不加无敌", "Spawn invincibility\nseconds of invincibility after each spawn\nchallenge and freeplay; 0 = none");
            SRApi.LocKey(Sec, "Auto Respawn", "自由模式自动重生", null);
            SRApi.LocDesc(Sec, "Auto Respawn", "自由模式自动重生\n自由模式死亡后自动重生（按重生延迟等待）\n挑战模式请用快速调整页的死后自动重试", "Auto respawn in freeplay\nrespawns automatically after death in freeplay, honouring the delay\nfor challenge use auto retry on the quick-adjust tab");
            SRApi.LocKey(Sec, "Cannon Auto Respawn", "炮弹卡死自动重生", null);
            SRApi.LocDesc(Sec, "Cannon Auto Respawn", "炮弹卡死自动重生\n死了还卡在炮弹里超过 1.5 秒时自动重生\n挑战/自由模式", "Respawn when stuck in a cannon\nrespawns if you stay dead inside a cannon for over 1.5s\nchallenge and freeplay");
            SRApi.LocKey(Sec, "No Death Scoreboard", "挑战死亡不弹记分板", null);
            SRApi.LocDesc(Sec, "No Death Scoreboard", "挑战死亡不弹记分板\n挑战模式死亡时不显示记分板\n只在死亡且尚未通关时生效", "No scoreboard on death\nskips the scoreboard when you die in challenge mode\nonly while dead and not yet succeeded");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                SRApi.RegisterPage(Sec, "respawn", "重生", "Respawn", 30, RenderRespawn);
                _delay = plugin.Config.Bind(Sec, "Respawn Delay", 0f, new ConfigDescription("重生延迟（0-3 秒）", new AcceptableValueRange<float>(0f, 3f)));
                _invincible = plugin.Config.Bind(Sec, "Spawn Invincible", 0f, new ConfigDescription("出生无敌（0-10 秒）", new AcceptableValueRange<float>(0f, 10f)));
                _autoRespawn = plugin.Config.Bind(Sec, "Auto Respawn", false, "自由模式：死亡后自动重生（按重生延迟等待）。");
                _cannonRespawn = plugin.Config.Bind(Sec, "Cannon Auto Respawn", false, "炮弹卡死：死亡且卡在炮弹里超过 1.5 秒自动重生。");
                _noScoreboard = plugin.Config.Bind(Sec, "No Death Scoreboard", false, "挑战模式死亡时不弹记分板。");
                SRApi.RegisterTick("重生-轮询", Tick);
                try { Harmony.CreateAndPatchAll(typeof(Respawn)); }
                catch (Exception __h) { SRApi.Guard.Log("重生/补丁注册", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("重生/初始化", __ex); }
        }

        private static void RenderRespawn() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("重生", "Respawn") + " -",
                    SRApi.T("挑战/自由模式通用：重生延迟、出生无敌、炮弹卡死自动重生、挑战死亡不弹记分板。",
                            "Challenge and freeplay: respawn delay, spawn invincibility, cannon stuck respawn and no death scoreboard."));
                if (_delay != null) SRApi.Ctl.RowSlider(SRApi.T("重生延迟", "Respawn delay"),
                    SRApi.T("死亡后等多少秒再自动重生\n0 = 立即\n挑战/自由模式", "seconds to wait after death\n0 = instant\nchallenge and freeplay"),
                    _delay, 0f, 3f, "0.0");
                if (_invincible != null) SRApi.Ctl.RowSlider(SRApi.T("出生无敌", "Spawn invincibility"),
                    SRApi.T("每次出生后的无敌秒数\n0 = 不加无敌\n挑战/自由模式", "seconds of invincibility after each spawn\n0 = none\nchallenge and freeplay"),
                    _invincible, 0f, 10f, "0.0");
                ConfigEntryBase ar = SRApi.Ctl.FindEntry(Sec, "Auto Respawn");
                if (ar != null) SRApi.Ctl.LabeledRow(SRApi.T("自由模式自动重生", "Auto respawn (freeplay)"),
                    SRApi.T("自由模式死亡后自动重生\n按上面的重生延迟等待\n挑战模式用「挑战」页的死后自动重试", "respawns automatically after death in freeplay\nhonours the delay above\nfor challenge use auto retry on the Challenge tab"), ar, 150f);
                ConfigEntryBase cr = SRApi.Ctl.FindEntry(Sec, "Cannon Auto Respawn");
                if (cr != null) SRApi.Ctl.LabeledRow(SRApi.T("炮弹卡死自动重生", "Cannon auto respawn"),
                    SRApi.T("死了还卡在炮弹里超过 1.5 秒时自动重生\n挑战/自由模式", "respawns if you stay dead inside a cannon for over 1.5s\nchallenge and freeplay"), cr, 150f);
                ConfigEntryBase ns = SRApi.Ctl.FindEntry(Sec, "No Death Scoreboard");
                if (ns != null) SRApi.Ctl.LabeledRow(SRApi.T("挑战死亡不弹记分板", "No death scoreboard"),
                    SRApi.T("挑战模式死亡时不显示记分板\n只在死亡且尚未通关时生效", "skips the scoreboard when you die in challenge only while dead and not yet succeeded"), ns, 150f);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/渲染页", __ex); }
        }

        private static Character LocalCharacter() {
            try {
                foreach (Character c in SRApi.FrameObjects<Character>()) {
                    if (c == null) continue;
                    if (c.hasAuthority) return c;
                    Player lp = c.LocalPlayer;
                    if (lp != null && lp.PlayerCharacter == c) return c;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生/查找本地角色", __ex); }
            return null;
        }

        // QuickAdjust 的自动重试调用：返回 true 表示还在延迟窗口内，本次不要发重试。
        internal static bool ShouldWaitForDelay(int networkNumber) {
            try {
                float d = DelaySeconds;
                if (d <= 0.01f) return false;
                float since;
                if (!_deadSince.TryGetValue(networkNumber, out since)) { _deadSince[networkNumber] = Time.unscaledTime; return true; }
                if (Time.unscaledTime - since < d) return true;
                _deadSince.Remove(networkNumber);
                return false;
            } catch (Exception __ex) { SRApi.Guard.Log("重生/延迟判定", __ex); return false; }
        }

        private static void Tick() {
            try {
                if (!SRApi.GateMaster || !ModeOk()) { _cannonStuckAt = -1f; return; }
                Character c = LocalCharacter();
                if (c == null) return;
                bool dead = c.Dead || c.Dying || c.LocallyDead;
                if (!dead) { _cannonStuckAt = -1f; return; }

                // 炮弹卡死：死了还留在炮弹里超过 1.5 秒 -> 重生
                if (_cannonRespawn != null && _cannonRespawn.Value && c.InCannon) {
                    if (_cannonStuckAt < 0f) _cannonStuckAt = Time.unscaledTime;
                    else if (Time.unscaledTime - _cannonStuckAt > 1.5f) {
                        _cannonStuckAt = -1f;
                        RespawnNow(c, "炮弹卡死");
                    }
                } else {
                    _cannonStuckAt = -1f;
                }

                // 自由模式自动重生（挑战由 QuickAdjust 的自动重试负责）
                if (_autoRespawn != null && _autoRespawn.Value) {
                    GameSettings gs = GameSettings.GetInstance();
                    if (gs != null && gs.GameMode == GameState.GameMode.FREEPLAY) {
                        int num = c.networkNumber;
                        float since;
                        if (!_deadSince.TryGetValue(num, out since)) { _deadSince[num] = Time.unscaledTime; return; }
                        if (Time.unscaledTime - since >= DelaySeconds) {
                            _deadSince.Remove(num);
                            RespawnNow(c, "自由模式自动重生");
                        }
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生/轮询", __ex); }
        }

        private static void RespawnNow(Character c, string why) {
            try {
                if (c == null) return;
                SRApi.LogInfo("[重生] " + why + "：发起重生");
                c.CallCmdRespawn();     // 游戏原生命令，房主/客户端均由服务器裁决
            } catch (Exception __ex) { SRApi.Guard.Log("重生/发起重生", __ex); }
        }

        // 出生无敌：Character.Enable 后给本地角色挂无敌计时（挑战/自由模式）
        [HarmonyPatch(typeof(Character), "Enable")]
        [HarmonyPostfix]
        private static void AfterEnable(Character __instance) {
            try {
                if (_invincible == null || !SRApi.GateMaster || !ModeOk()) return;
                float t = Mathf.Clamp(_invincible.Value, 0f, 10f);
                if (t <= 0.01f || __instance == null) return;
                Player lp = __instance.LocalPlayer;
                bool mine = __instance.hasAuthority || (lp != null && lp.PlayerCharacter == __instance);
                if (!mine) return;
                __instance.StartInvincibleTimer(t);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/出生无敌", __ex); }
        }

        // 挑战死亡不弹记分板：只在"挑战模式 + 本地角色死亡且未通关"时跳过
        [HarmonyPatch(typeof(BaseScoreboard), "Show")]
        [HarmonyPrefix]
        private static bool SkipDeathScoreboard() {
            try {
                if (_noScoreboard == null || !_noScoreboard.Value || !SRApi.GateMaster) return true;
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null || gs.GameMode != GameState.GameMode.CHALLENGE) return true;
                Character c = LocalCharacter();
                if (c == null) return true;
                bool dead = c.Dead || c.Dying || c.LocallyDead;
                return !(dead && !c.Success);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/跳过记分板", __ex); return true; }
        }
    }
}
