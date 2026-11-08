// 快速调整 · 重生延迟（自由模式/挑战）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    // 重生页面的 UI（重生延迟 / 出生无敌 / 炮弹卡死）也归这里。
    internal static partial class RespawnTools {

        // 「死亡无计分板」功能已删除（用户要求），这里保留空实现以免改动调用点。
        internal static void RenderNoScoreboardRow() { return; }

        private static void RenderNoScoreboardRowOld() {
            try {
                ConfigEntryBase ns = SRApi.Ctl.FindEntry(Sec, "No Death Scoreboard");
                if (ns != null) QuickAdjust.PageRow(SRApi.T("死亡无计分板", "No death scoreboard"),
                    SRApi.T("挑战模式死亡时不显示记分板\n只在死亡且尚未通关时生效", "skips the scoreboard when you die in challenge only while dead and not yet succeeded"), ns);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/记分板行", __ex); }
        }

        // 由「挑战」页调用：重生分区（跟在挑战分区下面）
        internal static void RenderRespawnSection() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("重生", "Respawn") + " -",
                    SRApi.T("重生延迟、出生无敌、炮弹卡死自动重生（挑战/自由通用）。",
                            "Respawn delay, spawn invincibility and cannon-stuck respawn (challenge and freeplay)."));
                if (_delay != null) QuickAdjust.PageSlider(SRApi.T("重生延迟", "Respawn delay"),
                    SRApi.T("死亡后等多少秒再自动重生\n默认值就是游戏原版数值，可自行调大调小\n0 = 立即重生", "seconds to wait after death before respawning\ndefaults to the game's own value; adjust freely\n0 = respawn instantly"),
                    _delay, 0f, 3f, "0.0");
                if (_invincible != null) QuickAdjust.PageSlider(SRApi.T("出生无敌", "Spawn invincibility"),
                    SRApi.T("每次出生后的无敌秒数\n游戏原生重生后是 1 秒\n挑战/自由模式", "seconds of invincibility after each spawn\nvanilla gives 1s after respawn\nchallenge and freeplay"),
                    _invincible, 0f, 10f, "0.0");
                ConfigEntryBase cr = SRApi.Ctl.FindEntry(Sec, "Cannon Auto Respawn");
                if (cr != null) QuickAdjust.PageRow(SRApi.T("炮弹卡死重生", "Cannon auto respawn"),
                    SRApi.T("死了还卡在炮弹里超过 1.5 秒时自动重生\n挑战/自由模式", "respawns if you stay dead inside a cannon for over 1.5s\nchallenge and freeplay"), cr);
            } catch (Exception __ex) { SRApi.Guard.Log("重生/分区渲染", __ex); }
        }

        // 首次同步标记：把游戏原版的重生延迟填进配置
        internal static bool _respawnDelaySynced;

    }

//   死亡后 Character 会等到 deathTimer > maxDeathDelay（或 minDeathDelay + 落地静置）才真正判死
//   随后 FreePlayControl.Update 立刻复活 —— 所以把这三个值设成目标秒数就是原生延迟
// 关闭时把首次读到的原值写回去
internal static class RespawnDelayTimingPatch {
    private static readonly Dictionary<Character, float[]> _orig = new Dictionary<Character, float[]>();

    [HarmonyPatch(typeof(Character), "Update")]
    [HarmonyPrefix]
    private static void Prefix(Character __instance) {
        try {
            if (__instance == null || !__instance.hasAuthority) return;
            if (!SRApi.GateMaster) { Restore(__instance); return; }
            float d = RespawnTools.RespawnDelaySeconds;
            // 还没同步：把这个人物的原版 maxDeathDelay 当作"原版数值"写进配置（只做一次）。
            if (d < 0f) {
                if (!RespawnTools._respawnDelaySynced) {
                    RespawnTools._respawnDelaySynced = true;
                    float vanilla = __instance.minDeathDelay;
                    if (vanilla >= 0f && vanilla <= 3f) {
                        try { RespawnTools.SetDelayFromVanilla(vanilla); } catch { /* 静默：写配置失败不影响功能 */ }
                    }
                }
                Restore(__instance);
                return;
            }
            bool want = !__instance.Success
                && SRApi.GateModeAllows(SRApi.ModeMask.Freeplay | SRApi.ModeMask.Challenge);
            if (!want) { Restore(__instance); return; }
            float[] o;
            if (!_orig.TryGetValue(__instance, out o)) {
                o = new float[] { __instance.maxDeathDelay, __instance.minDeathDelay, __instance.minSkipDeathTime };
                _orig[__instance] = o;
            }
            __instance.maxDeathDelay = d;
            __instance.minDeathDelay = d;
            __instance.minSkipDeathTime = d;
        } catch (Exception __ex) { SRApi.Guard.Log("重生延迟/计时阈值", __ex); }
    }

    private static void Restore(Character c) {
        float[] o;
        if (c == null || !_orig.TryGetValue(c, out o)) return;
        _orig.Remove(c);
        if (o == null || o.Length < 3) return;
        try { c.maxDeathDelay = o[0]; c.minDeathDelay = o[1]; c.minSkipDeathTime = o[2]; }
        catch { /* 静默：角色销毁中，无需还原 */ }
    }
}
}
