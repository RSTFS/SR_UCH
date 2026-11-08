// 快速调整 · 重生延迟（自由模式/挑战）。
// 参考 RespawnControl：改死亡计时阈值，由游戏自己按这个时间重生。
// 从 QuickAdjust.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    internal static partial class RespawnTools {
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
