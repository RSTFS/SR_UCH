// 快速调整 · 快速切换 / 快速重试：按住 B 的阈值覆盖、自动重试发送。
// 从 QuickAdjust.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class QuickAdjust {
        // 长按 B 阈值：自由/挑战模式固定 Character.SuicideTime = 0.5s；
        // 建造态切回行动用 PiecePlacementCursor.SwitchTime（取 prefab 序列化值，读不到按 0.5s）。
        public static string VanillaHoldBText(bool forSwitch) {
            if (_vanFrame == Time.frameCount) {
                string hit = forSwitch ? _vanSwCache : _vanRetryCache;
                if (hit != null) return hit;
            }
            string s = ComputeVanilla(forSwitch);
            if (forSwitch) _vanSwCache = s; else _vanRetryCache = s;
            _vanFrame = Time.frameCount;
            return s;
        }

        private static string ComputeVanilla(bool forSwitch) {
            string s = "0.5s";
            if (!forSwitch) return s;
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null || p.AssociatedGamePlayer == null) continue;
                    PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
                    if (pc == null || pc.SwitchTime <= 0f) continue;
                    if (Mathf.Abs(pc.SwitchTime - 0.5f) > 0.05f) {
                        string st = pc.SwitchTime.ToString("0.0");
                        s += SRApi.T("（建造->行动 " + st + "s）", " (build->action " + st + "s)");
                    }
                    break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("读取 SwitchTime 默认值", __ex); }
            return s;
        }

        // UpdateHoldBIndicator 是设置 SuicideTime 的地方，Postfix 覆盖为配置值，并同步建造态 SwitchTime。
        [HarmonyPatch(typeof(Character), "UpdateHoldBIndicator")]
        [HarmonyPostfix]
        private static void OverrideHoldTime(Character __instance)
        {
            if (!SRApi.GateMaster || (SRApi.Typing) || !IsLocalCharacter(__instance)) return;
            try
            {
                // 模式门控统一走 GateModeAllows（已含模式豁免）。
                bool freeplayHold = QuickSwitchOn && SRApi.GateModeAllows(SRApi.ModeMask.Freeplay);
                bool challengeHold = !freeplayHold && QuickRetryFastOn && SRApi.GateModeAllows(SRApi.ModeMask.Challenge);
                if (freeplayHold)
                {
                    __instance.SuicideTime = QuickSwitchHold; // 自由模式长按 B 的切换等待
                    // 建造态切回行动用 PiecePlacementCursor.SwitchTime，须每帧同步。
                    // 一帧内多个 Character 都会进这里，整表扫描按帧只做一次。
                    if (_switchTimeFrame != Time.frameCount) {
                        _switchTimeFrame = Time.frameCount;
                        foreach (Player p in PlayerManager.GetInstance()) {
                            if (p == null || p.AssociatedGamePlayer == null) continue;
                            PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
                            if (pc == null) continue;
                            if (!_switchTimeOrig.ContainsKey(pc)) _switchTimeOrig[pc] = pc.SwitchTime;
                            pc.SwitchTime = QuickSwitchHold;
                        }
                    }
                }
                else if (challengeHold)
                {
                    __instance.SuicideTime = QuickRetryHold;
                }
                else
                {
                    // 两个功能都没生效时**一个字都不写**：SuicideTime 是游戏按模式自己设的
                    // （自由模式 2 秒、挑战 0.5 秒…），以前这里无条件写回首次抓到的"原值"，
                    // 结果所有模式都被钉成同一个数（用户反馈：各模式长按 B 全变成 2 秒）。
                    RestoreSwitchTime();
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.OverrideHoldTime", __ex); }
        }

        private static bool ShouldWaitRespawnDelay(int num) {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry("Quick Adjust", "Respawn Delay");
                float d = 0f;
                try { if (e != null) d = System.Convert.ToSingle(e.BoxedValue); } catch (Exception __cv) { SRApi.Guard.Log("快速调整/读重生延迟", __cv); }
                if (d <= 0.01f) { _deadSinceMap.Remove(num); return false; }
                float since;
                if (!_deadSinceMap.TryGetValue(num, out since)) { _deadSinceMap[num] = Time.unscaledTime; return true; }
                if (Time.unscaledTime - since < d) return true;
                _deadSinceMap.Remove(num);
                return false;
            } catch (Exception __ex) { SRApi.Guard.Log("快速调整/重生延迟判定", __ex); return false; }
        }

        // 是否是"我这边的角色"：房主端 hasAuthority，房客端 hasAuthority 可能为 false，
        // 但 LocalPlayer.PlayerCharacter 指向的就是自己的角色。以前只判 hasAuthority，
        // 于是"在别人房间死后不自动重试"。两个补丁都用这个判定。
        private static bool IsLocalCharacter(Character c) {
            try {
                if (c == null) return false;
                if (c.hasAuthority) return true;
                Player lp = c.LocalPlayer;
                return lp != null && lp.PlayerCharacter == c;
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust/本地角色判定", __ex); return false; }
        }

        [HarmonyPatch(typeof(Character), "UpdateSuicidalState")]
        [HarmonyPostfix]
        private static void ForceSuicideState(Character __instance)
        {
            if (!SRApi.GateMaster || (SRApi.Typing) || !IsLocalCharacter(__instance))
            {
                return;
            }
            // 模式门控统一走 GateModeAllows（已含模式豁免，模式取不到时一律不放行）。
            if (SRApi.GateModeAllows(SRApi.ModeMask.Challenge))
            {
                bool dead = !__instance.Success && (__instance.Dead || __instance.Dying || __instance.LocallyDead);
                int num;
                try { num = __instance.networkNumber; }
                catch { return; }
                if (!dead)
                {
                    // 不再处于死亡状态（复活/回合重置/换关）：解锁本角色闩锁。
                    _retrySentTo.Remove(num);
                    return;
                }
                // 一次性闩锁：WantsToRetry 是 SyncVar，从发出到服务器回写为真的这段时间本方法每帧都跑。
                // 按 networkNumber 记录（同局多个 Character，单个 static bool 会被队友清掉而退化成每帧发包）。
                if (_retrySentTo.Contains(num)) return;
                if (!QuickRetryOn || __instance.WantsToRetry) return;
                if (!SRApi.GateModeAllows(SRApi.ModeMask.Challenge)) return;   // 用户要求：自动/快速重试仅挑战模式
                // 重生延迟：还在延迟窗口内就先不发送。
                // 直接读配置条目（不引用「重生」功能的类型）：拆分构建下两者在不同 DLL，编译期不能互相引用。
                if (ShouldWaitRespawnDelay(num)) return;
                _retrySentTo.Add(num);
                if (_retrySentTo.Count > 64) _retrySentTo.Clear();
                try
                {
                    MsgPlayerWantsToRetry msg = new MsgPlayerWantsToRetry { networkNumber = num };
                    UnityEngine.Networking.NetworkManager.singleton.client.Send(NetMsgTypes.PlayerWantsToRetry, msg);
                }
                catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
                return;
            }
            if (!SRApi.GateModeAllows(SRApi.ModeMask.Freeplay))
            {
                return;
            }
            // 自由模式：等待时间实际由 OverrideHoldTime 设置。
            if (!QuickSwitchOn) return;
            // 仅对局中生效，树屋/大厅不处理。
            try
            {
                if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null) return;
            }
            catch
            {
                return;
            }
            try { __instance.SuicideTime = QuickSwitchHold; } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
            // 建造态 SwitchTime 同样设为配置值。
            try {
                // 按帧只做一次：一帧内多个 Character 回调都会进这里。
                if (_switchTimeFrame != Time.frameCount) {
                    _switchTimeFrame = Time.frameCount;
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.AssociatedGamePlayer == null) continue;
                        PiecePlacementCursor pc = p.AssociatedGamePlayer.CursorInstance as PiecePlacementCursor;
                        if (pc != null) pc.SwitchTime = QuickSwitchHold;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
        }
    }
}
