using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class ModManager {

// ==== 分区：Reload（重载关卡保分补分 / 折扣保留 / 重载宽限 / 加载后 GC）====

        //加载完成：回收加载产生的垃圾（临时分配），减少进入对局后的卡顿。
        //只在本场景第一次加载完成时清理（进关卡/换关卡）；同关卡回合切换重载时场景名不变 → 跳过，
        //避免每回合 GC.Collect + UnloadUnusedAssets 拖慢回合结算（还会卸载马上重用的资产）。
        //延迟 1 秒执行：GC.Collect 是同步阻塞（几十~几百 ms），若在 FadeOut 时立即执行会卡住
        //"加载画面淡出→进入对局"的过渡；延后到对局已开始后再清理，过渡流畅且清理效果不变。
        private static string _lastCleanedScene = "";
        private static float _pendingCleanupAt = -1f;
        private static string _pendingCleanupScene = "";
        [HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
        [HarmonyPostfix]
        static void OnLoadEnd() {
            //重载关卡后广播补分（模式一"保留分数"：按原类型分块补分，下一回合结算显示）
            FillScoresIfPending();
            //重载关卡后恢复分数折扣（handicap）：无论模式一/二，房主写回 SyncVar → 房客折扣保留
            RestoreHandicapsIfAny();
            if (!ModManager.AllEnabled) return;
            if (_gcAfterLoadEntry == null || !_gcAfterLoadEntry.Value) return;
            try {
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (sc == _lastCleanedScene) return; //同场景（回合切换重载）不清理
                //延迟 1 秒执行（进入对局后），不阻塞 FadeOut 过渡
                _pendingCleanupScene = sc;
                _pendingCleanupAt = Time.unscaledTime + 1f;
            } catch { }
        }

        //--- 场景重载期间暂停"坏客户端踢出"检测 ---
        //LobbyManager.Update 每 1 秒跑 DisconnectBrokenClients：把连续 3 秒没有有效
        //player controller 的连接踢掉。广播方块快照 / 平板重载等 ReloadScene 重载场景时，
        //客户端正在加载新场景、player controller 暂时无效——若重载稍慢（如本 Mod 的
        //「加载后清理」GC 拖慢），客户端会被房主误判为坏连接直接断开（表现为 Steam 断连弹窗）。
        //修复：ReloadScene 后给踢人检测一个宽限期（默认 20 秒，足够全员重载完成）。
        private static float _reloadGraceUntil = 0f;
        [HarmonyPatch(typeof(LobbyManager), "ReloadScene")]
        [HarmonyPrefix]
        static void OnReloadSceneStart() {
            _reloadGraceUntil = Time.unscaledTime + 20f;
        }
        [HarmonyPatch(typeof(LobbyManager), "DisconnectBrokenClients")]
        [HarmonyPrefix]
        static bool SkipDisconnectDuringReload() {
            return Time.unscaledTime >= _reloadGraceUntil;
        }

        //--- 广播快照重载后保留分数（补分方案，分类型） ---
        //重载关卡 = ReloadScene 重载场景 → 新场景 VersusControl.SetupStart →
        //ScoreKeeper.Setup() 把 playerTotal/分块清空（"重开本关"的固有行为）→ 分数丢失。
        //重载关卡前：备份各玩家的**分块列表**（含类型：win/coin/trap...），
        //重载完成后房主按**原类型**逐个广播 PointAwarded 重放 → 全员（含无 mod 房客）
        //addPointBlock → 下一回合结算 tally 进 playerTotal → 得分板显示与重载前完全一致的分数与类型。
        //不恢复 playerTotal（那只有房主端有效）；补分后不触发立即结算（跳过结算 → 图标不爆屏）。
        private static bool _preserveScoresOnReload = false;
        //备份：networkNumber → List<PointBlock>（含类型的完整分块）
        private static Dictionary<int, List<PointBlock>> _scoreBackupBlocks = null;
        //补分重放后置位：下一次结算的 ClearNewPointBlocks 跳过清除，保证重放分块
        //（含 second/third/fourth 等非 AlwaysAward）能正常显示+tally，不被再次清掉。
        private static bool _refillPending = false;
        //外部加分（EX 加分 / 本模块补分重放）的分块标记：MsgPointAwarded.AlwaysAward=true。
        //结算 ClearNewPointBlocks 会删掉非 AlwaysAward 分块（second/third/fourth 名次分等），
        //原生发的这类分块从未 tally → 不该补（重放会多计分）；但 EX 加分走 AlwaysAward=true
        //消息，玩家在得分板明确看到了图标、期望保留 → 被 Clear 时缓存，重载备份补回。
        //key = (playerNumber << 8) | (int)type
        private static HashSet<int> _externAwardedKeys = new HashSet<int>();
        //结算时被 Clear 的 EX 加分分块（重载备份时合并进 _scoreBackupBlocks）
        private static List<PointBlock> _clearedExternBlocks = new List<PointBlock>();
        //备份：networkNumber → handicap（房客也可能有分数折扣；LobbyPlayer/GamePlayer
        //都是 DontDestroyOnLoad，SyncVar 本应跨场景保留，但保险起见重载后由房主
        //服务器端显式写回 SyncVar 广播全员 → 重载关卡后房客折扣也保留）
        private static Dictionary<int, int> _handicapBackup = null;

        //重载关卡前直接置标志（模式一：允许补分）
        public static void MarkPreserveScores() {
            _preserveScoresOnReload = true;
        }

        [HarmonyPatch(typeof(GameControl), "handleEvent")]
        [HarmonyPrefix]
        static void OnPrepareReloadMessage(GameEvent.GameEvent e) {
            try {
                //收到 PrepareToReloadScene（房主 SendToAll，房主本地也会收到）：
                //仅模式一（保留分数）才置补分标志；模式二（仅保留方块）保持分数重置。
                if (e is GameEvent.NetworkMessageReceivedEvent nm && nm.Message.msgType == NetMsgTypes.PrepareToReloadScene) {
                    if (Experiments.ReloadKeepsScore) _preserveScoresOnReload = true;
                    //新一次重载：重置补分保护标志（防止上次残留影响本次）
                    _refillPending = false;
                    //折扣备份与补分无关：无论哪种模式，重载后房客/房主的分数折扣都要保留。
                    //房主端备份（服务器权威值）；房客端也备份无妨（恢复仅房主执行）。
                    BackupHandicaps();
                }
            } catch { }
        }

        //备份所有在线玩家的 handicap（从 LobbyPlayer 读服务器权威值）。
        private static void BackupHandicaps() {
            try {
                _handicapBackup = new Dictionary<int, int>();
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.PlayerTracker == null) return;
                for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                    try {
                        NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                        LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(info.NetworkNumber);
                        if (lp == null) continue;
                        _handicapBackup[info.NetworkNumber] = lp.Networkhandicap;
                    } catch { }
                }
                if (_handicapBackup.Count > 0) {
                    MainPlugin.ModLogger.LogInfo("[折扣] 重载前备份 " + _handicapBackup.Count + " 名玩家 handicap");
                }
            } catch { }
        }

        //重载完成后：房主把备份的 handicap 写回（LobbyPlayer + GamePlayer 的 SyncVar，
        //服务器端赋值自动广播全员；两者均 DontDestroyOnLoad，写回后跨场景持续生效）。
        private static void RestoreHandicapsIfAny() {
            if (_handicapBackup == null || _handicapBackup.Count == 0) return;
            Dictionary<int, int> backup = _handicapBackup;
            _handicapBackup = null;
            try {
                if (!NetworkServer.active) return; //仅房主写 SyncVar
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.PlayerTracker == null) return;
                foreach (KeyValuePair<int, int> kv in backup) {
                    try {
                        LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(kv.Key);
                        if (lp != null) lp.Networkhandicap = kv.Value;
                    } catch { }
                    try {
                        GamePlayer gp = lm.PlayerTracker.GetGamePlayer(kv.Key);
                        if (gp != null) gp.NetworkHandicap = kv.Value;
                    } catch { }
                }
                MainPlugin.ModLogger.LogInfo("[折扣] 重载后恢复 " + backup.Count + " 名玩家 handicap");
            } catch { }
        }

        //结算时 ClearNewPointBlocks 会删掉非 AlwaysAward 分块（second/third/fourth 名次分等）。
        //1) 补分重放后（_refillPending）：跳过清除，保证重放分块正常显示+tally；
        //2) 正常结算：只缓存"外部加分"（EX 加分/补分重放，MsgPointAwarded.AlwaysAward=true）
        //   被清的分块——玩家在得分板明确看到了这些分块图标、期望保留，重载必须补回。
        //   原生发的非 AlwaysAward 分块（AlwaysAward=false）从未 tally、玩家从未看到 → 不缓存（避免多计分）。
        [HarmonyPatch(typeof(ScoreKeeper), "ClearNewPointBlocks")]
        [HarmonyPrefix]
        static bool ClearNewPointBlocksPrefix(ScoreKeeper __instance) {
            try {
                if (_refillPending) {
                    //补分重放后的第一次结算：跳过清除，保留全部分块
                    return false;
                }
                if (__instance == null || __instance.newPointBlocks == null) return true;
                for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                    PointBlock pb = __instance.newPointBlocks[i];
                    if (pb == null) continue;
                    if (!pb.AlwaysAward && _externAwardedKeys.Contains(Key(pb))) {
                        //外部加分（EX/补分）的非 AlwaysAward 分块：缓存，重载备份时补回
                        _clearedExternBlocks.Add(pb);
                    }
                }
                if (_clearedExternBlocks.Count > 500) {
                    _clearedExternBlocks.RemoveRange(0, _clearedExternBlocks.Count - 500);
                }
            } catch { }
            return true;
        }

        //分块标记键：(playerNumber << 8) | (int)type
        private static int Key(PointBlock pb) { return (pb.playerNumber << 8) | (int)pb.type; }
        private static int Key(int playerNumber, PointBlock.pointBlockType type) { return (playerNumber << 8) | (int)type; }

        //ScoreKeeper.handleEvent 收到 PointAwarded 消息：标记"外部加分"分块。
        //EX 加分与本模块补分重放都发 MsgPointAwarded.AlwaysAward=true；
        //原生（DoPlayMode 名次分/金币等）发的 AlwaysAward 由分块类型计算属性决定（名次分为 false）。
        //这样能精确区分"玩家看到的加分"（要保留）与"原生无效分"（Clear 掉不补）。
        [HarmonyPatch(typeof(ScoreKeeper), "handleEvent")]
        [HarmonyPrefix]
        static void OnPointAwardedMessage(ScoreKeeper __instance, global::GameEvent.GameEvent e) {
            try {
                if (e == null || e.GetType() != typeof(GameEvent.NetworkMessageReceivedEvent)) return;
                GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
                if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.PointAwarded) return;
                MsgPointAwarded msg = nm.Message.ReadMessage<MsgPointAwarded>();
                if (msg != null && msg.AlwaysAward) {
                    _externAwardedKeys.Add(Key(msg.PlayerNumber, msg.PointType));
                }
            } catch { }
        }

        //补分重放的分块在结算 tally 后即完成使命；此时清除 _refillPending 与外部加分标记，
        //之后恢复正常清除（已 tally 的分块不再需要保护/缓存）。
        [HarmonyPatch(typeof(ScoreKeeper), "TallyPointBlockAllPlayers")]
        [HarmonyPostfix]
        static void TallyPostfix() {
            _refillPending = false;
            _externAwardedKeys.Clear();
            _clearedExternBlocks.Clear();
        }

        [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
        [HarmonyPrefix]
        static void ScoreSetupPrefix(ScoreKeeper __instance) {
            if (!_preserveScoresOnReload) return;
            try {
                //备份 historyPointBlocks（已 tally 的分块，对应 playerTotal 已计分）
                //+ newPointBlocks（本回合未结算的分块）。这两者才是玩家重载前真正拥有的分。
                List<PointBlock> blocks = new List<PointBlock>();
                try {
                    FieldInfo hf = AccessTools.Field(typeof(ScoreKeeper), "historyPointBlocks");
                    List<PointBlock> hist = hf != null ? hf.GetValue(__instance) as List<PointBlock> : null;
                    if (hist != null) blocks.AddRange(hist);
                } catch (Exception ex) {
                    MainPlugin.ModLogger.LogWarning("[保分] 备份 historyPointBlocks 失败: " + ex.Message);
                }
                try {
                    FieldInfo nf = AccessTools.Field(typeof(ScoreKeeper), "newPointBlocks");
                    List<PointBlock> npb = nf != null ? nf.GetValue(__instance) as List<PointBlock> : null;
                    if (npb != null) blocks.AddRange(npb);
                } catch (Exception ex) {
                    MainPlugin.ModLogger.LogWarning("[保分] 备份 newPointBlocks 失败: " + ex.Message);
                }
                if (_clearedExternBlocks != null && _clearedExternBlocks.Count > 0) {
                    //结算时被 Clear 的"外部加分"分块（EX 加分/补分重放的 second/third/fourth 等，
                    //玩家在得分板看到过、期望保留）——合并进备份，重载后补回
                    blocks.AddRange(_clearedExternBlocks);
                    MainPlugin.ModLogger.LogInfo("[保分] 合并被清除的外部加分分块 " + _clearedExternBlocks.Count + " 条");
                    _clearedExternBlocks.Clear();
                }
                if (blocks.Count == 0) {
                    MainPlugin.ModLogger.LogWarning("[保分] 重载时无可备份的分块（history+new+外部加分均为空）");
                    return;
                }
                //按玩家分组（networkNumber）
                _scoreBackupBlocks = new Dictionary<int, List<PointBlock>>();
                foreach (PointBlock pb in blocks) {
                    if (pb == null) continue;
                    List<PointBlock> list;
                    if (!_scoreBackupBlocks.TryGetValue(pb.playerNumber, out list)) {
                        list = new List<PointBlock>();
                        _scoreBackupBlocks[pb.playerNumber] = list;
                    }
                    list.Add(pb);
                }
                MainPlugin.ModLogger.LogInfo("[保分] 重载备份分块 " + blocks.Count + " 条，涉及 " + _scoreBackupBlocks.Count + " 名玩家");
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("[保分] 备份分块失败: " + ex.Message);
            }
        }

        [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
        [HarmonyPostfix]
        static void ScoreSetupPostfix(ScoreKeeper __instance) {
            //重载保留分数模式：下一次结算跳过 ClearNewPointBlocks（保护重放分块不被再次清除）
            //正常开局（非重载）时 _preserveScoresOnReload=false → 同步清掉 _refillPending，
            //避免上次重载残留的 true 影响本次正常结算。
            _refillPending = _preserveScoresOnReload;
            //只清标志，不恢复分块——分块图标由补分广播恢复（见 FillScoresIfPending）
            _preserveScoresOnReload = false;
        }

        //重载关卡完成后：广播补分（模式一）。房主按备份的**原类型分块**逐个广播 PointAwarded，
        //AlwaysAward=true（ClearNewPointBlocks 保留），下一回合结算 tally 时进 playerTotal。
        //防网络风暴：总上限 300 条，分帧发送（每帧 30 条），避免一次性塞爆 UNET 消息队列。
        private const int FillPerFrame = 30;
        private const int FillTotalCap = 300;
        private static void FillScoresIfPending() {
            if (_scoreBackupBlocks == null || _scoreBackupBlocks.Count == 0) return;
            if (!AllEnabled) return;
            Dictionary<int, List<PointBlock>> backup = _scoreBackupBlocks;
            _scoreBackupBlocks = null;
            try {
                if (!NetworkServer.active) return; //仅房主广播
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.client == null || !lm.client.isConnected) return;
                //扁平化：networkNumber → 分块类型序列
                List<KeyValuePair<int, PointBlock.pointBlockType>> queue = new List<KeyValuePair<int, PointBlock.pointBlockType>>();
                foreach (KeyValuePair<int, List<PointBlock>> kv in backup) {
                    List<PointBlock> list = kv.Value;
                    if (list == null || list.Count == 0) continue;
                    foreach (PointBlock pb in list) {
                        if (pb == null) continue;
                        if (queue.Count >= FillTotalCap) break; //全局上限
                        queue.Add(new KeyValuePair<int, PointBlock.pointBlockType>(kv.Key, pb.type));
                    }
                }
                if (queue.Count == 0) return;
                //分帧发送：借 LobbyManager 的协程运行器
                lm.StartCoroutine(FillScoresCoroutine(queue));
                MainPlugin.ModLogger.LogInfo("[补分] 队列 " + queue.Count + " 条，分帧发送（每帧 " + FillPerFrame + " 条）");
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("[补分] 初始化失败: " + ex.Message);
            }
        }

        private static System.Collections.IEnumerator FillScoresCoroutine(List<KeyValuePair<int, PointBlock.pointBlockType>> queue) {
            int sent = 0;
            while (sent < queue.Count) {
                int batch = Mathf.Min(FillPerFrame, queue.Count - sent);
                for (int i = 0; i < batch; i++) {
                    try {
                        LobbyManager lm = LobbyManager.instance;
                        if (lm == null || lm.client == null || !lm.client.isConnected) yield break;
                        lm.client.Send(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                            PlayerNumber = queue[sent].Key, PointType = queue[sent].Value, AlwaysAward = true
                        });
                    } catch (Exception ex) {
                        MainPlugin.ModLogger.LogWarning("[补分] 发送失败: " + ex.Message);
                    }
                    sent++;
                }
                yield return null; //每帧发一批，避免网络风暴
            }
        }

	}
}
