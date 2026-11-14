// 快速调整 · 重生延迟（自由模式/挑战）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    // 重生页面的 UI（重生延迟 / 出生无敌 / 炮弹卡死）也归这里。
    internal static partial class RespawnTools {

        // 由「挑战」页调用：重生分区（跟在挑战分区下面）
        internal static void RenderRespawnSection() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("重生", "Respawn") + " -",
                    SRApi.T("重生延迟、出生无敌、炮弹卡死自动重生（挑战/自由通用）。",
                            "Respawn delay, spawn invincibility and cannon-stuck respawn (challenge and freeplay)."));
                if (_delay != null) {
                    // 默认 0.5 秒；点击标签恢复默认（核心 RowSlider 语义）。
                    SRApi.Ctl.RowSlider(SRApi.T("重生延迟", "Respawn delay"),
                        SRApi.T("死亡后等多少秒再自动重生\n0 = 立即重生\n点击本行标签恢复默认",
                                "seconds to wait after death before respawning\n0 = respawn instantly\nclick this label to restore the default"),
                        _delay, 0f, 3f, "0.0", QuickAdjust.PageLabelWidth() / Mathf.Max(0.0001f, SRApi.Ctl.Sc(1f)));
                }
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
        // 读到的游戏原版重生延迟（-1 = 还没读到）；用于在页面上显示"游戏默认"的实际值。
        internal static float SyncedVanillaDelay = -1f;

    }

//   死亡后 Character 会等到 deathTimer > maxDeathDelay（或 minDeathDelay + 落地静置）才真正判死
//   随后 FreePlayControl.Update 立刻复活 —— 所以把这三个值设成目标秒数就是原生延迟
// 关闭时把首次读到的原值写回去
internal static class RespawnDelayTimingPatch {
    // 原值快照按「角色 + 当前模式」存：不同模式的原版死亡计时不同（自由/挑战/派对/创造各不相同），
    // 只按角色存会让"先在挑战开过、再回自由关掉"还原成挑战的值。
    private static readonly Dictionary<long, float[]> _orig = new Dictionary<long, float[]>();

    // 当前模式位（GateModeAllows 对所有"模式位掩码"回答 true 表示该位命中；超级模式会让多位同时为真，不影响一致性）。
    private static int ModeKey() {
        int m = 0;
        if (SRApi.GateModeAllows(SRApi.ModeMask.Freeplay)) m |= 1;
        if (SRApi.GateModeAllows(SRApi.ModeMask.Challenge)) m |= 2;
        if (SRApi.GateModeAllows(SRApi.ModeMask.Party)) m |= 4;
        if (SRApi.GateModeAllows(SRApi.ModeMask.Creative)) m |= 8;
        return m;
    }
    private static long KeyOf(Character c, int modeKey) {
        return ((long)c.GetInstanceID() << 8) | (long)(modeKey & 0xFF);
    }

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
            long key = KeyOf(__instance, ModeKey());
            float[] o;
            if (!_orig.TryGetValue(key, out o)) {
                o = new float[] { __instance.maxDeathDelay, __instance.minDeathDelay, __instance.minSkipDeathTime };
                if (_orig.Count > 64) _orig.Clear();   // 换场景/换角色后按 size 兜底清理
                _orig[key] = o;
            }
            __instance.maxDeathDelay = d;
            __instance.minDeathDelay = d;
            __instance.minSkipDeathTime = d;
        } catch (Exception __ex) { SRApi.Guard.Log("重生延迟/计时阈值", __ex); }
    }

    private static void Restore(Character c) {
        if (c == null) return;
        long key = KeyOf(c, ModeKey());
        float[] o;
        if (!_orig.TryGetValue(key, out o)) return;
        _orig.Remove(key);
        if (o == null || o.Length < 3) return;
        try { c.maxDeathDelay = o[0]; c.minDeathDelay = o[1]; c.minSkipDeathTime = o[2]; }
        catch { /* 静默：角色销毁中，无需还原 */ }
    }
}
}
