// 关卡工具页（侧栏「关卡」第 2 页）：重载关卡 / 广播方块快照 / 重载模式 / 重载保分补分 / 清场槽位 / 快照后处理补丁。
// 命名空间内 Level 会遮蔽游戏自带的 global::Level。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
namespace SR_UCH.Tweaks {
public partial class Level {
    private static ConfigEntry<ReloadMode> _reloadMode;


    public static void RenderTools() {
        SRApi.Ctl.SectionTitle(SRApi.T("- 关卡工具 -", "- Level tools -"));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("level.reload", SRApi.T("重载关卡", "Reload level"),
            SRApi.T("房主把当前看到的方块都写进关卡快照, 然后真的重载一次当前关卡, 全员一起重新加载.\n重载完按快照把方块重建出来并广播, 玩家放的方块和能动的道具都不会丢.\n要不要顺手把分数补回来, 看下面那个重载模式.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host writes the current blocks into the snapshot and really reloads the level scene, so everyone reloads.\nBlocks are rebuilt from the snapshot and nothing is lost.\nWhether the score is filled back depends on the Reload mode below.\n Party/Creative matches only, host only."),
            () => Level.ReloadLevel(), SRApi.Ctl.Sc(170), SRApi.Ctl.Sc(30))) {
            Level.ReloadLevel();
        }
        ConfigEntryBase rlm = SRApi.Ctl.FindEntry("Level", "Reload Mode");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("重载模式", "Reload mode"),
            SRApi.T("重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按原类型分块给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks kept; the host fills scores so everyone sees the same score/types next tally.\nKeep blocks only (skip fill) = blocks kept, score resets.")),
            rlm, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (rlm != null) SRApi.Ctl.RenderControl(rlm);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("level.broadcast", SRApi.T("广播方块快照", "Broadcast snapshot"),
            SRApi.T("房主把当前看到的方块连位置, 旋转和属性一起打包广播出去, 全员按房主那边的样子重建方块.\n局内偶尔出现的方块自己这边看不见或者不同步, 就用这个修.\n不会重载场景: 对局进度和分数都留着, 全员会卡一下, 然后方块就回来了.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host packages the blocks they see and broadcasts them, so everyone rebuilds from the host snapshot.\nUse this when blocks occasionally disappear or desync on your side mid-match.\nNo scene reload: progress and scores stay.\n Party/Creative matches only, host only."),
            () => Level.BroadcastSnapshot(), SRApi.Ctl.Sc(170), SRApi.Ctl.Sc(30))) {
            Level.BroadcastSnapshot();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(4));
    }

		// 重载关卡模式：保留方块与分数 / 仅保留方块（分数重置）。
		public enum ReloadMode {
			KeepScore,
			KeepBlocksOnly
		}

    public static void MarkPreserveScores() {
        _preserveScoresOnReload = true;
        _preserveMarkedAt = Time.unscaledTime;
    }

    [HarmonyPatch(typeof(GameControl), "handleEvent")]
    [HarmonyPrefix]
    static void OnPrepareReloadMessage(GameEvent.GameEvent e) {
        try {
            // 收到 PrepareToReloadScene 只重置补分保护，不置补分标志，否则外部工具发起的重载会再补一份导致分数翻倍。
            if (e is GameEvent.NetworkMessageReceivedEvent nm && nm.Message.msgType == NetMsgTypes.PrepareToReloadScene) {
                _refillPending = false;
                // 折扣备份与补分模式无关；恢复仅房主执行，房客端备份无副作用。
                BackupHandicaps();
            }
        } catch (Exception __ex) { SRApi.Guard.Log("处理 PrepareToReloadScene", __ex); }
    }

    // 备份所有在线玩家的 handicap（从 LobbyPlayer 读服务器权威值）。
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
                } catch (Exception __ex) { SRApi.Guard.Log("读取玩家 handicap", __ex); }
            }
            if (_handicapBackup.Count > 0) {
                SRApi.LogInfo("[折扣] 重载前备份 " + _handicapBackup.Count + " 名玩家 handicap");
            }
        } catch (Exception __ex) { SRApi.Guard.Log("备份 handicap", __ex); }
    }

    // 重载完成后房主写回备份的 handicap（LobbyPlayer + GamePlayer 的 SyncVar，服务器赋值自动广播全员）。
    private static void RestoreHandicapsIfAny() {
        if (_handicapBackup == null || _handicapBackup.Count == 0) return;
        Dictionary<int, int> backup = _handicapBackup;
        _handicapBackup = null;
        try {
            if (!SRApi.HasServer) return; // 仅房主可写 SyncVar
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.PlayerTracker == null) return;
            foreach (KeyValuePair<int, int> kv in backup) {
                try {
                    LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(kv.Key);
                    if (lp != null) lp.Networkhandicap = kv.Value;
                } catch (Exception __ex) { SRApi.Guard.Log("恢复 LobbyPlayer handicap", __ex); }
                try {
                    GamePlayer gp = lm.PlayerTracker.GetGamePlayer(kv.Key);
                    if (gp != null) gp.NetworkHandicap = kv.Value;
                } catch (Exception __ex) { SRApi.Guard.Log("恢复 GamePlayer handicap", __ex); }
            }
            SRApi.LogInfo("[折扣] 重载后恢复 " + backup.Count + " 名玩家 handicap");
        } catch (Exception __ex) { SRApi.Guard.Log("恢复 handicap", __ex); }
    }

    // ClearNewPointBlocks 会删掉非 AlwaysAward 分块：补分重放后（_refillPending）跳过清除；正常结算只缓存外部加分分块，原生未 tally 的不缓存以免多计分。
    [HarmonyPatch(typeof(ScoreKeeper), "ClearNewPointBlocks")]
    [HarmonyPrefix]
    static bool ClearNewPointBlocksPrefix(ScoreKeeper __instance) {
        try {
            if (_refillPending) {
                // 跳过清除分支同样要缓存（去重后合并），否则同一批 AlwaysAward 分块会在下次重载备份时被补回两次。
                try {
                    if (__instance != null && __instance.newPointBlocks != null) {
                        // 按 Key 线性去重（条数受 FillTotalCap 限制）
                        for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                            PointBlock pb = __instance.newPointBlocks[i];
                            if (pb == null) continue;
                            int k = Key(pb);
                            bool dup = false;
                            for (int j = 0; j < _clearedExternBlocks.Count; j++) {
                                if (_clearedExternBlocks[j] != null && Key(_clearedExternBlocks[j]) == k) { dup = true; break; }
                            }
                            if (!dup) _clearedExternBlocks.Add(pb);
                        }
                    }
                } catch (Exception __cex) { SRApi.Guard.Log("缓存补分分块", __cex); }
                // 补分重放后的第一次结算跳过清除，保留全部分块
                return false;
            }
            if (__instance == null || __instance.newPointBlocks == null) return true;
            for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                PointBlock pb = __instance.newPointBlocks[i];
                if (pb == null) continue;
                if (!pb.AlwaysAward && _externAwardedKeys.Contains(Key(pb))) {
                    // 外部加分的非 AlwaysAward 分块：缓存，重载备份时补回
                    _clearedExternBlocks.Add(pb);
                }
            }
            if (_clearedExternBlocks.Count > 500) {
                _clearedExternBlocks.RemoveRange(0, _clearedExternBlocks.Count - 500);
            }
        } catch (Exception __ex) { SRApi.Guard.Log("缓存被清除的外部加分分块", __ex); }
        return true;
    }

    // 分块标记键：(playerNumber << 8) | (int)type
    private static int Key(PointBlock pb) { return (pb.playerNumber << 8) | (int)pb.type; }
    private static int Key(int playerNumber, PointBlock.pointBlockType type) { return (playerNumber << 8) | (int)type; }

    // 标记外部加分分块：外部模块加分与补分重放都发 AlwaysAward=true，原生名次分等为 false。
    [HarmonyPatch(typeof(ScoreKeeper), "handleEvent")]
    [HarmonyPrefix]
    static void OnPointAwardedMessage(ScoreKeeper __instance, global::GameEvent.GameEvent e) {
        try {
            if (e == null || e.GetType() != typeof(GameEvent.NetworkMessageReceivedEvent)) return;
            GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
            if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.PointAwarded) return;
            // reader 已被 GameState.distributeMessage 消费，再 ReadMessage 会越界，必须用事件里已反序列化的字段。
            MsgPointAwarded msg = nm.ReadMessage as MsgPointAwarded;
            if (msg != null && msg.AlwaysAward) {
                _externAwardedKeys.Add(Key(msg.PlayerNumber, msg.PointType));
            }
        } catch (Exception __ex) { SRApi.Guard.Log("标记外部加分分块", __ex); }
    }

    // 分块 tally 后清除 _refillPending 与外部加分标记，恢复正常清除。
    [HarmonyPatch(typeof(ScoreKeeper), "TallyPointBlockAllPlayers")]
    [HarmonyPostfix]
    static void TallyPostfix() {
        try {
                _refillPending = false;
                _externAwardedKeys.Clear();
                _clearedExternBlocks.Clear();
        } catch (Exception __ex) { SRApi.Guard.Log("结算收尾", __ex); }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPrefix]
    static void ScoreSetupPrefix(ScoreKeeper __instance) {
        if (!_preserveScoresOnReload) return;
        try {
            // 备份 historyPointBlocks（已 tally）+ newPointBlocks（本回合未结算）。
            List<PointBlock> blocks = new List<PointBlock>();
            try {
                FieldInfo hf = SRApi.Ref.Field(typeof(ScoreKeeper), "historyPointBlocks", "重载保分（历史分块）");
                List<PointBlock> hist = hf != null ? hf.GetValue(__instance) as List<PointBlock> : null;
                if (hist != null) blocks.AddRange(hist);
            } catch (Exception ex) {
                SRApi.LogWarn("[保分] 备份 historyPointBlocks 失败: " + ex.Message);
            }
            try {
                List<PointBlock> npb = __instance.newPointBlocks; // public 字段，直接读
                if (npb != null) blocks.AddRange(npb);
            } catch (Exception ex) {
                SRApi.LogWarn("[保分] 备份 newPointBlocks 失败: " + ex.Message);
            }
            if (_clearedExternBlocks != null && _clearedExternBlocks.Count > 0) {
                // 结算时被 Clear 的外部加分分块：合并进备份，重载后补回
                blocks.AddRange(_clearedExternBlocks);
                SRApi.LogInfo("[保分] 合并被清除的外部加分分块 " + _clearedExternBlocks.Count + " 条");
                _clearedExternBlocks.Clear();
            }
            if (blocks.Count == 0) {
                SRApi.LogWarn("[保分] 重载时无可备份的分块（history+new+外部加分均为空）");
                return;
            }
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
            SRApi.LogInfo("[保分] 重载备份分块 " + blocks.Count + " 条，涉及 " + _scoreBackupBlocks.Count + " 名玩家");
        } catch (Exception ex) {
            SRApi.LogWarn("[保分] 备份分块失败: " + ex.Message);
        }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPostfix]
    static void ScoreSetupPostfix(ScoreKeeper __instance) {
        try {
                // 重载保分模式：下次结算跳过 ClearNewPointBlocks；超时未消费的保分标志按普通开局处理，避免补回陈旧分块。
                bool fresh = _preserveScoresOnReload && _preserveMarkedAt >= 0f
                    && (Time.unscaledTime - _preserveMarkedAt) <= PreserveStaleAfter;
                if (_preserveScoresOnReload && !fresh) {
                    SRApi.LogWarn("[保分] 保分标志已过期（" + ((int)(Time.unscaledTime - _preserveMarkedAt))
                        + " 秒没被结算），本次按普通开局处理");
                }
                _refillPending = fresh;
                // 只清标志，不恢复分块（分块图标由 FillScoresIfPending 的补分广播恢复）
                _preserveScoresOnReload = false;
                _preserveMarkedAt = -1f;
        } catch (Exception __ex) { SRApi.Guard.Log("保分标志复位", __ex); }
    }

    // 重载后广播补分：房主按备份的原类型逐个发 PointAwarded（AlwaysAward=true），下回合 tally 进 playerTotal。
    // 防网络风暴：上限 FillTotalCap 条，每帧 FillPerFrame 条。
    private const int FillPerFrame = 30;
    private const int FillTotalCap = 300;
    private static void FillScoresIfPending() {
        if (_scoreBackupBlocks == null || _scoreBackupBlocks.Count == 0) return;
        // 只以服务器权威把关（SRApi.HasServer），不用 UI 总开关，否则关掉总开关会让分数补不回来。
        if (!SRApi.HasServer) return;
        Dictionary<int, List<PointBlock>> backup = _scoreBackupBlocks;
        _scoreBackupBlocks = null;
        try {
            // 必须走 NetworkServer.SendToAll（服务器 -> 全员）；lm.client.Send 是 client->server 方向，房客的 ScoreKeeper 收不到。
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.client == null || !lm.client.isConnected) return;
            List<KeyValuePair<int, PointBlock.pointBlockType>> queue = new List<KeyValuePair<int, PointBlock.pointBlockType>>();
            // 备份含跨回合累积的 historyPointBlocks，长局易超上限；截断时必须告警丢了多少。
            int total = 0;
            foreach (KeyValuePair<int, List<PointBlock>> kv in backup) {
                List<PointBlock> list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++) if (list[i] != null) total++;
            }
            bool capped = false;
            foreach (KeyValuePair<int, List<PointBlock>> kv in backup) {
                List<PointBlock> list = kv.Value;
                if (list == null || list.Count == 0) continue;
                foreach (PointBlock pb in list) {
                    if (pb == null) continue;
                    if (queue.Count >= FillTotalCap) { capped = true; break; } // 全局上限
                    queue.Add(new KeyValuePair<int, PointBlock.pointBlockType>(kv.Key, pb.type));
                }
                if (capped) break; // 已达上限，外层同步停止
            }
            if (capped) {
                SRApi.LogWarn("[补分] 备份分块 " + total + " 条超过上限 " + FillTotalCap
                    + "，仅补回前 " + queue.Count + " 条（" + (total - queue.Count) + " 条被丢弃，重载后分数会偏少）");
            }
            if (queue.Count == 0) return;
            lm.StartCoroutine(FillScoresCoroutine(queue));
            SRApi.LogInfo("[补分] 队列 " + queue.Count + " 条，分帧发送（每帧 " + FillPerFrame + " 条）");
        } catch (Exception ex) {
            SRApi.LogWarn("[补分] 初始化失败: " + ex.Message);
        }
    }

    private static System.Collections.IEnumerator FillScoresCoroutine(List<KeyValuePair<int, PointBlock.pointBlockType>> queue) {
        int sent = 0;
        while (sent < queue.Count) {
            int batch = Mathf.Min(FillPerFrame, queue.Count - sent);
            for (int i = 0; i < batch; i++) {
                try {
                    // 服务器权威广播：房客的 ScoreKeeper 收到后自行进入 newPointBlocks，非权威端等广播即可。
                    if (!NetworkServer.active) { SRApi.LogWarn("[补分] 已非服务器权威，中止补分"); yield break; }
                    NetworkServer.SendToAll(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                        PlayerNumber = queue[sent].Key, PointType = queue[sent].Value, AlwaysAward = true
                    });
                } catch (Exception ex) {
                    SRApi.LogWarn("[补分] 发送失败: " + ex.Message);
                }
                sent++;
            }
            yield return null; // 每帧发一批
        }
    }
}
}