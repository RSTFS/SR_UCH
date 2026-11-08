// 快速调整 · 评分百分比（分数折扣 / 记分板 handicap 刷新）。
// 从 QuickAdjust.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class QuickAdjust {
        private static void TickKeepHandicap() {
            try {
                if (_keepHandicap <= 0) return;
                if (Time.unscaledTime - _keepLastAt < 1f) return;
                _keepLastAt = Time.unscaledTime;
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null) continue;
                    LobbyPlayer lp = null;
                    try { lp = p.AssociatedLobbyPlayer; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    if (lp == null || !lp.IsLocalPlayer) continue;
                    try { if (lp.Networkhandicap != _keepHandicap) lp.SetPlayerHandicap(_keepHandicap); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    try { if (p.AssociatedGamePlayer != null && p.AssociatedGamePlayer.Handicap != _keepHandicap) p.AssociatedGamePlayer.CallCmdSetPlayerHandicap(_keepHandicap); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.TickKeepHandicap", __ex); }
        }

        // 每 0.5 秒检查一次本地 GamePlayer 的 handicap，被新关卡重置就重新下发（原生 Command）。
        public static void ApplyScoreDiscount()
        {
            try
            {
                if (!SRApi.GateMaster)
                {
                    SRApi.Notify(SRApi.MsgMasterOff);
                    return;
                }
                int target = Mathf.Clamp(HandicapValue, 10, 100);
                if (ApplyOwnHandicap(target))
                {
                    // 核实：读回本地 LobbyPlayer 的 handicap，值不对就明确提示（而不是静默成功）。
                    try {
                        LobbyPlayer __lp = FindLocalLobbyPlayer();
                        if (__lp != null && __lp.handicap != target) {
                            SRApi.LogWarn("[快速调整] 已下发 " + target + "%，但本地读回 " + __lp.handicap + "%（可能被服务器或规则覆盖）");
                        }
                    } catch (Exception __v) { SRApi.Guard.Log("QuickAdjust/核实 handicap", __v); }
                    // 100 也要记住：100% = 不折扣，但它是当前生效值。以前把它归零等于放弃保持，
                    // 角色重建/重生后可能被大厅里的旧值盖回去（audit C-9）。
                    _keepHandicap = target;
                    SRApi.Notify(SRApi.T("评分百分比：", "Score percentage: ") + target + "%");
                }
                else
                {
                    SRApi.Notify(SRApi.T("评分百分比应用失败", "Failed to apply score percentage"));
                }
            }
            catch (Exception ex)
            {
                SRApi.LogWarn((object)("评分折扣失败: " + ex.Message));
            }
        }

        // 同时写 LobbyPlayer（平衡板/下一局）与 GamePlayer（本局立即生效）。
        // 原生方案：直接改当前房间本地玩家的 LobbyPlayer.handicap（服务器权威值，重载场景/回合都保留）。
        // 换房间 = 新的 LobbyPlayer（默认 100），折扣自然重置；不做任何每帧/每局自动重放。
        // 与树屋评分板（游戏里的 handicap 组件）完全同一套写法：
        // 1) 发原生的 PlayerHandicapSet 消息（服务器会转发给全员，客户端据此播动画/音效）；
        // 2) 调本地 LobbyPlayer.SetPlayerHandicap（原生 Command 写 SyncVar，整个房间/后续回合都保留）。
        private static bool ApplyOwnHandicap(int value)
        {
            try
            {
                value = Mathf.Clamp(value, 10, 100);
                LobbyPlayer lp = FindLocalLobbyPlayer();
                if (lp == null) return false;
                SendNativeHandicapMessage(lp, value);
                lp.SetPlayerHandicap(value);
                // 局内：同步当前 GamePlayer，让平衡板立刻刷新（原生评分板只在树屋，无此步）。
                SyncLocalGamePlayerHandicap(lp, value);
                return true;
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.ApplyOwnHandicap", __ex); }
            return false;
        }

        // 原生广播：与 game 的 handicap.sendHandicapMessage 一致（Action=NONE 只同步数值，不触发动画/音效）。
        private static void SendNativeHandicapMessage(LobbyPlayer lp, int value)
        {
            try
            {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.client == null || !lm.client.isConnected) return;
                MsgPlayerHandicapSet msg = new MsgPlayerHandicapSet();
                msg.Handicap = value;
                msg.NetworkPlayerNumber = lp.networkNumber;
                msg.Action = handicap.HandicapAction.NONE;
                lm.client.Send(NetMsgTypes.PlayerHandicapSet, msg);
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust/SendNativeHandicapMessage", __ex); }
        }

        // 取本地 LobbyPlayer：优先“本地 Player 的 AssociatedLobbyPlayer”（树屋评分板就是这么取角色对应的平衡板行），
        // 退而求其次用 PlayerTracker / 场景内 IsLocalPlayer 的 LobbyPlayer。
        private static LobbyPlayer FindLocalLobbyPlayer()
        {
            try
            {
                foreach (Player p in PlayerManager.GetInstance())
                {
                    try
                    {
                        if (p != null && p.AssociatedLobbyPlayer != null && p.AssociatedLobbyPlayer.IsLocalPlayer)
                            return p.AssociatedLobbyPlayer;
                    }
                    catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(Player)", __ex); }
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer", __ex); }
            try
            {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null)
                {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++)
                    {
                        try
                        {
                            NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                            LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(info.NetworkNumber);
                            if (lp != null && lp.IsLocalPlayer) return lp;
                        }
                        catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(tracker)", __ex); }
                    }
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(tracker)", __ex); }
            try
            {
                foreach (LobbyPlayer lp in UnityEngine.Object.FindObjectsOfType<LobbyPlayer>())
                {
                    if (lp != null && lp.IsLocalPlayer) return lp;
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.FindLocalLobbyPlayer(scene)", __ex); }
            return null;
        }

        // 已在本局里：同步 GamePlayer，让平衡板立刻刷新（不改的话等下一局才变）。
        private static void SyncLocalGamePlayerHandicap(LobbyPlayer lp, int value)
        {
            try
            {
                foreach (Player p in PlayerManager.GetInstance())
                {
                    if (p == null || p.AssociatedLobbyPlayer != lp) continue;
                    if (p.AssociatedGamePlayer != null)
                    {
                        try { p.AssociatedGamePlayer.CallCmdSetPlayerHandicap(value); }
                        catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.CallCmdSetPlayerHandicap", __ex); }
                    }
                    break;
                }
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.SyncLocalGamePlayerHandicap", __ex); }
        }

        public static void RestoreScoreDiscount()
        {
            try
            {
                if (!SRApi.GateMaster)
                {
                    SRApi.Notify(SRApi.MsgMasterOff);
                    return;
                }
                if (ApplyOwnHandicap(100))
                {
                    _keepHandicap = 0;
                    SRApi.Notify(SRApi.T("评分百分比：100%", "Score percentage: 100%"));
                }
                else
                {
                    SRApi.Notify(SRApi.T("评分百分比还原失败", "Failed to restore score percentage"));
                }
            }
            catch (Exception ex)
            {
                SRApi.LogWarn((object)("恢复评分折扣失败: " + ex.Message));
            }
        }


        [HarmonyPatch(typeof(GamePlayer), "set_NetworkHandicap")]
        [HarmonyPostfix]
        private static void RefreshScoreboardHandicap(GamePlayer __instance) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        // 服务端/显式设置走 set_NetworkHandicap，房客端由 OnDeserialize 兜底。
                        RefreshScoreboardHandicapIfChanged(__instance);
            } catch (Exception __ex) { SRApi.Guard.Log("分数折扣刷新(服务端)", __ex); }
        }

        // 房客端 SyncVar 反序列化直接写 Handicap 字段，不走 set_NetworkHandicap，须在此补刷。
        [HarmonyPatch(typeof(GamePlayer), "OnDeserialize")]
        [HarmonyPostfix]
        private static void RefreshScoreboardHandicapClient(GamePlayer __instance) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        RefreshScoreboardHandicapIfChanged(__instance);
            } catch (Exception __ex) { SRApi.Guard.Log("分数折扣刷新(房客)", __ex); }
        }

        // 统一入口：handicap 与上次记录不同才刷新。
        private static void RefreshScoreboardHandicapIfChanged(GamePlayer __instance) {
            try {
                if (__instance == null) return;
                int h = __instance.Handicap;
                int prev;
                if (_lastHandicapShown.TryGetValue(__instance, out prev) && prev == h) return;
                // 先刷新、成功后再记录：Core 在计分板未就绪时会提前返回，先记录会导致该值不再重试。
                if (!RefreshScoreboardHandicapCore(__instance)) return;
                _lastHandicapShown[__instance] = h;
                // 超限整体清空（场景切换后 GamePlayer 重建，旧引用残留）。
                if (_lastHandicapShown.Count > 64) _lastHandicapShown.Clear();
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.RefreshScoreboardHandicapCore", __ex); }
        }

        private static bool RefreshScoreboardHandicapCore(GamePlayer __instance) {
            try {
                if (__instance == null) return false;
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return false;
                GameControl gc = lm.CurrentGameController as GameControl;
                if (gc == null) return false;
                GraphScoreBoard board = null;
                try {
                    VersusControl vc = gc as VersusControl;
                    if (vc != null) {
                        if (_gsbField == null) _gsbField = SRApi.Ref.Field(typeof(VersusControl), "graphScoreBoardInstance", "评分折扣（取计分板实例）");
                        if (_gsbField != null) board = _gsbField.GetValue(vc) as GraphScoreBoard;
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.RefreshScoreboardHandicapCore", __ex); }
                if (board == null) {
                    board = UnityEngine.Object.FindObjectOfType<GraphScoreBoard>();
                }
                if (board == null) return false;
                if (_relField == null) _relField = SRApi.Ref.Field(typeof(GraphScoreBoard), "scorelineRelation", "评分折扣（分数条位置）");
                if (_relField == null) return false;
                object rel = _relField.GetValue(board);
                if (rel == null) return false;
                IDictionary<int, ScoreLine> relDict = rel as IDictionary<int, ScoreLine>;
                if (relDict == null) return false;
                ScoreLine line = null;
                int num = 0;
                try { num = __instance.NetworknetworkNumber; } catch { num = __instance.networkNumber; }
                if (!relDict.TryGetValue(num, out line)) {
                    try {
                        line = relDict[__instance.localNumber];
                    } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.GetValue", __ex); }
                }
                if (line == null) return false;
                line.SetHandicap(__instance.Handicap);
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.SetHandicap", __ex); return false; }
        }
    }
}
