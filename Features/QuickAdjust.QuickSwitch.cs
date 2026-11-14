// 快速调整 · 快速切换 / 快速重试：长按 B 阈值覆盖与自动重试发送。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class QuickAdjust {
        // 长按 B 阈值：自由/挑战模式固定 SuicideTime = 0.5s；建造态另用 SwitchTime。
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

        // UpdateHoldBIndicator 是设 SuicideTime 处，Postfix 覆盖为配置值并同步 SwitchTime。
        [HarmonyPatch(typeof(Character), "UpdateHoldBIndicator")]
        [HarmonyPostfix]
        private static void OverrideHoldTime(Character __instance)
        {
            if (!SRApi.GateMaster || (SRApi.Typing) || !IsLocalCharacter(__instance)) return;
            try
            {
                bool freeplayHold = QuickSwitchOn && SRApi.GateModeAllows(SRApi.ModeMask.Freeplay);
                bool challengeHold = !freeplayHold && QuickRetryFastOn && SRApi.GateModeAllows(SRApi.ModeMask.Challenge);
                if (freeplayHold)
                {
                    __instance.SuicideTime = QuickSwitchHold; // 自由模式长按 B 的切换等待
                    // 建造态 SwitchTime 须每帧同步；整表扫描按帧只做一次。
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
                    // 两功能都未生效时不写 SuicideTime，让游戏按模式自行设置。
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

        // 是否本机角色：房客端 hasAuthority 可能为 false，故再比 LocalPlayer.PlayerCharacter。
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
            if (SRApi.GateModeAllows(SRApi.ModeMask.Challenge))
            {
                bool dead = !__instance.Success && (__instance.Dead || __instance.Dying || __instance.LocallyDead);
                int num;
                try { num = __instance.networkNumber; }
                catch { return; }
                if (!dead)
                {
                    _retrySentTo.Remove(num);
                    return;
                }
                // 一次性闩锁：WantsToRetry 为 SyncVar，回写前本方法每帧都跑，不锁会每帧发包。
                // 按 networkNumber 记录；同局多角色共用静态 bool 会被队友清掉。
                if (_retrySentTo.Contains(num)) return;
                if (!QuickRetryOn || __instance.WantsToRetry) return;
                if (!SRApi.GateModeAllows(SRApi.ModeMask.Challenge)) return;   // 自动/快速重试仅挑战模式
                // 重生延迟窗口内先不发送。
                // 直接读配置条目：拆分构建下与「重生」功能不在同一 DLL，不能互相引用。
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
            if (!QuickSwitchOn) return;
            try
            {
                if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null) return;
            }
            catch
            {
                return;
            }
            try { __instance.SuicideTime = QuickSwitchHold; } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.Send", __ex); }
            try {
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
