// 快速调整 · 评分百分比：分数折扣下发与记分板 handicap 刷新。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class QuickAdjust {
        // 换关卡/换房间即失效：百分比只在一局内保持，不再跨房延续。
        private static string _keepRoom;
        private static void ResetHandicapOnRoomChange() {
            try {
                string room = CurrentRoomKey();
                if (room == _keepRoom) return;
                _keepRoom = room;
                if (_keepHandicap > 0) {
                    _keepHandicap = 0;               // 停止保持
                    try { ApplyOwnHandicap(100); } catch (Exception __rh) { SRApi.Guard.Log("评分百分比/换房还原", __rh); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("评分百分比/换房检测", __ex); }
        }

        // 当前"房间 + 关卡"标识：大厅会话 ID 与场景名任一变化都算换房。
        private static string CurrentRoomKey() {
            string lobby = "";
            try {
                // 本机玩家的节点 ID（同房间内稳定，换房/重连会变）+ 房主 GSID 共同标识房间。
                LobbyPlayer lp = FindLocalLobbyPlayer();
                if (lp != null) lobby = lp.playerNodeID.ToString();
                if (LobbyManager.instance != null && LobbyManager.instance.lobbySlots != null) {
                    for (int i = 0; i < LobbyManager.instance.lobbySlots.Length; i++) {
                        LobbyPlayer slot = LobbyManager.instance.lobbySlots[i] as LobbyPlayer;
                        if (slot != null && slot.IsHost) { lobby += "@" + (slot.GSID ?? ""); break; }
                    }
                }
            } catch (Exception __rk) { SRApi.Guard.Log("评分百分比/房号", __rk); }
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch (Exception __sc) { SRApi.Guard.Log("评分百分比/场景名", __sc); }
            return lobby + "|" + scene;
        }

        private static void TickKeepHandicap() {
            try {
                ResetHandicapOnRoomChange();
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

        // 每 0.5 秒核对本地 handicap，被新关卡重置则重新下发（原生 Command）。
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
                    // 读回本地 handicap 核对，不一致时明确告警。
                    try {
                        LobbyPlayer __lp = FindLocalLobbyPlayer();
                        if (__lp != null && __lp.handicap != target) {
                            SRApi.LogWarn("[快速调整] 已下发 " + target + "%，但本地读回 " + __lp.handicap + "%（可能被服务器或规则覆盖）");
                        }
                    } catch (Exception __v) { SRApi.Guard.Log("QuickAdjust/核实 handicap", __v); }
                    // 100% 也要记住：它是当前生效值，归零等于放弃保持。
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

        // 同时写 LobbyPlayer（下一局）与 GamePlayer（本局立即生效）；改的是服务器权威值。
        private static bool ApplyOwnHandicap(int value)
        {
            try
            {
                value = Mathf.Clamp(value, 10, 100);
                LobbyPlayer lp = FindLocalLobbyPlayer();
                if (lp == null) return false;
                SendNativeHandicapMessage(lp, value);
                lp.SetPlayerHandicap(value);
                // 局内同步 GamePlayer，让平衡板立刻刷新。
                SyncLocalGamePlayerHandicap(lp, value);
                return true;
            }
            catch (Exception __ex) { SRApi.Guard.Log("QuickAdjust.ApplyOwnHandicap", __ex); }
            return false;
        }

        // 原生广播（同 handicap.sendHandicapMessage）：Action=NONE 只同步数值，不播动画音效。
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

        // 取本地 LobbyPlayer：优先 AssociatedLobbyPlayer，再退到 PlayerTracker / 场景扫描。
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
            try {
                        RefreshScoreboardHandicapIfChanged(__instance);
            } catch (Exception __ex) { SRApi.Guard.Log("分数折扣刷新(服务端)", __ex); }
        }

        // 房客端 SyncVar 反序列化直接写字段，不走 set_NetworkHandicap，须在此补刷。
        [HarmonyPatch(typeof(GamePlayer), "OnDeserialize")]
        [HarmonyPostfix]
        private static void RefreshScoreboardHandicapClient(GamePlayer __instance) {
            try {
                        RefreshScoreboardHandicapIfChanged(__instance);
            } catch (Exception __ex) { SRApi.Guard.Log("分数折扣刷新(房客)", __ex); }
        }

        private static void RefreshScoreboardHandicapIfChanged(GamePlayer __instance) {
            try {
                if (__instance == null) return;
                int h = __instance.Handicap;
                int prev;
                if (_lastHandicapShown.TryGetValue(__instance, out prev) && prev == h) return;
                // 先刷新成功再记录：Core 在计分板未就绪时提前返回，先记录会导致不再重试。
                if (!RefreshScoreboardHandicapCore(__instance)) return;
                _lastHandicapShown[__instance] = h;
                // 超限整体清空：场景切换后 GamePlayer 重建会留下旧引用。
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
