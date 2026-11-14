// Level 分片：重载关卡（房主写快照 → ReloadScene → 全员重建）+ 重载期保分。
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
        private static bool _reloadBusy; // 重载进行中

        private static float _reloadBusyAt = -1f; // 加锁时刻（识别锁残留）

        private static void SetReloadBusy(bool busy)
        {
            _reloadBusy = busy;
            _reloadBusyAt = busy ? Time.unscaledTime : -1f;
        }

        // 锁残留自愈：协程被销毁时无人复位，超时仍锁着即视为残留。
        private static bool ReloadLockStuck()
        {
        return _reloadBusy && _reloadBusyAt >= 0f && Time.unscaledTime - _reloadBusyAt > SrTimeouts.ReloadLockStaleSeconds;
        }

        // 防重入判定：重载中提示并拦下；锁残留则放锁放行本次操作。
        private static bool ReloadBlocked()
        {
            // 先查对方：布局预设正在加载时必须拦下。
            try {
                Func<bool> other = Slots.Require<Func<bool>>("presets.reloadBusy");
                if (other != null && other()) {
                    SRApi.Notify(SRApi.T("布局预设正在加载，请稍候", "A layout preset is loading; please wait"));
                    return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重载互斥检查", __ex); }
            if (!_reloadBusy) return false;
            if (ReloadLockStuck()) {
                SetReloadBusy(false);
                SRApi.LogWarn("[重载] 检测到残留的忙碌锁（超过 45 秒未复位），已自动放锁");
                return false;
            }
            SRApi.Notify(SRApi.T("重载进行中，请稍候", "Reload in progress"));
            return true;
        }

        private static float _lastSnapshotAt = -999f; // 上次广播快照时刻

        private static float _reloadLoadedAt = -1f; // FadeOut 时刻（决定何时清 levelPortalXml）

        private static float _reloadStartedAt = -1f; // 发起 ReloadScene 时刻（超时兜底）

        public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

        // 仅派对 / 创意 / 自由建造关卡内生效（忽略模式限制的豁免已内置）。
        private static bool IsPartyCreativeOrFreeplay()
        {
            return SRApi.GateModeAllows(SRApi.ModeMask.Party | SRApi.ModeMask.Creative | SRApi.ModeMask.Freeplay);
        }

        // 重载当前关卡：先把房主快照写入 QuickSaver.levelPortalXml，重载后由 OnSetupStartLevel 读取广播。
        public static void ReloadLevel()
        {
            ReloadLevelImpl(SRApi.T("重载关卡", "Reload Level"));
        }

        private static void ReloadLevelImpl(string feature)
        {
            // 锁由协程末尾复位；提前放锁会让连点并发重载。
            bool coroutineStarted = false;
            try
            {
                if (!SRApi.GateMaster)
                {
                    SRApi.Notify(SRApi.MsgMasterOff);
                    return;
                }
                if (!IsPartyCreativeOrFreeplay())
                {
                    SRApi.Notify(feature + SRApi.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
                    return;
                }
                if (!SRApi.GateHost)
                {
                    SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
                    return;
                }
                // 必须服务器权威：开了无视房主限制的房客会过 GateHost，但网络调用在客户端不执行会假成功。
                if (!NetworkServer.active)
                {
                    SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
                    return;
                }
                LobbyManager lm = LobbyManager.instance;
                if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
                {
                    SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
                    return;
                }
                GameControl gc = lm.CurrentGameController as GameControl;
                if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
                {
                    SRApi.Notify(SRApi.T("未进入有效对局", "No active match"));
                    return;
                }
                QuickSaver qs = gc.GetComponent<QuickSaver>();
                if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
                {
                    SRApi.Notify(SRApi.T("当前对局不支持该操作", "Unsupported in the current match"));
                    return;
                }
                GameState gs = GameState.GetInstance();
                if (gs == null)
                {
                    SRApi.Notify(SRApi.T("游戏状态尚未就绪", "Game state not ready"));
                    return;
                }
                if (ReloadBlocked()) return; // 锁残留自愈
                // 快照刚广播（3 秒内）先别重载，等方块重建落定。
                if (Time.unscaledTime - _lastSnapshotAt < 3f) {
                    SRApi.Notify(SRApi.T("快照刚广播，请稍后重载", "Snapshot just broadcast; retry later"));
                    return;
                }
                SetReloadBusy(true);
                // 协程只在末尾复位锁；本方法所有提前 return 都在 StartCoroutine 之前，须逐条手动复位。
                System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
                if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
                {
                    SetReloadBusy(false); // 失败路径复位锁
                    SRApi.Notify(SRApi.T("当前场景没有可同步的方块", "No blocks to sync in the current scene"));
                    return;
                }
                try
                {
                    QuickSaver.levelPortalXml = doc.OuterXml; // public static 字段，直接写
                }
                catch
                {
                    SetReloadBusy(false); // 失败路径复位锁
                    SRApi.Notify(SRApi.T("快照写入失败", "Failed to write the snapshot"));
                    return;
                }
                // 补分标志须在真正 ReloadScene 前置位；提前置位会被提前返回留下残留，致下次结算错补分。
                // GameSettings 取不到必须中止并解锁：客户端收不到 PrepareToReloadScene 会与房主脱同步。
                GameSettings gsForMode = GameSettings.GetInstance();
                if (gsForMode == null)
                {
                    SRApi.Notify(SRApi.T("游戏加载中，请稍后再试",
                        "Still loading; try again later"));
                    SetReloadBusy(false);
                    return;
                }
                try
                {
                    MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene
                    {
                        reloadToMode = gsForMode.GameMode,
                        snapshotInfo = gs.currentSnapshotInfo
                    };
                    NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
                }
                catch (Exception __ex) { SRApi.Guard.Log("Level.SendToAll", __ex); }
                if (LoadingInterstitialSplash.Instance != null)
                {
                    LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
                    LoadingInterstitialSplash.Instance.FadeIn();
                }
                // 成败提示在协程内发出；模式随参数传入，协程内不再取 GameSettings（未判空会 NRE）。
                lm.StartCoroutine(ReloadSceneRoutine(feature, gsForMode.GameMode));
                coroutineStarted = true; // 锁复位交给协程末尾
            }
            catch (Exception ex)
            {
                if (!coroutineStarted) SetReloadBusy(false);
                SRApi.LogWarn((object)(feature + "失败: " + ex.Message));
            }
        }

        private static IEnumerator ReloadSceneRoutine(string feature, GameState.GameMode toMode)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame(); // 等 FadeIn 生效
            bool ok = false;
            try
            {
                LobbyManager lm = LobbyManager.instance;
                if ((UnityEngine.Object)(object)lm != (UnityEngine.Object)null)
                {
                    // 与原生重载一致：先重置方块 ID 序列，否则新 ID 与快照中的 parentID/mainID 错位。
                    try { Placeable.SetInitialSequenceID(0); } catch (Exception __ex) { SRApi.Guard.Log("重载前重置方块 ID 序列", __ex); }
                    if (ReloadKeepsScore) MarkPreserveScores();
                    lm.ReloadScene(toMode);
                    ok = true;
                }
            }
            catch (Exception ex)
            {
                SRApi.LogWarn((object)("重载场景失败: " + ex.Message));
            }
            // 收尾（清 levelPortalXml + 放锁）必须走 finally，否则异常会让锁一直挂着。
            try
            {
                if (ok)
                {
                    SRApi.Notify(feature + SRApi.T("：重载中，方块保留，", ": reloading, blocks kept, ") +
                        (ReloadKeepsScore ? SRApi.T("分数保留", "scores kept") : SRApi.T("分数重置", "scores reset")));
                }
                else
                {
                    SRApi.Notify(feature + SRApi.T("：重载失败", ": reload failed"));
                }
                _reloadStartedAt = Time.unscaledTime;
                _reloadLoadedAt = -1f; // 等 FadeOut 置位
                // 加载完成后等 2 秒再清 levelPortalXml 并放锁；FadeOut 不触发时按 SrTimeouts 兜底。
                if (ok)
                {
                    while (_reloadBusy)
                    {
                        if (_reloadLoadedAt >= 0f && Time.unscaledTime - _reloadLoadedAt >= 2f) break;
                        if (Time.unscaledTime - _reloadStartedAt >= SrTimeouts.PresetConsumeTimeoutSeconds) break;
                        yield return null;
                    }
                }
            }
            finally
            {
                try { ClearPortalXml(); } catch (Exception __cx) { SRApi.Guard.Log("重载收尾/清理预设XML", __cx); }
                SetReloadBusy(false);
            }
        }

        // 加载完成回调（FadeOut 时由重载钩子调用）：记录时刻供协程决定何时清 levelPortalXml。
        public static void OnReloadSceneLoaded()
        {
            _reloadLoadedAt = Time.unscaledTime;
        }

        private static void ClearPortalXml()
        {
            try
            {
                QuickSaver.levelPortalXml = null;
            }
            catch (Exception __ex) { SRApi.Guard.Log("Level.Field", __ex); }
        }

        // 重载宽限期：ReloadScene 后 20 秒内跳过断连检查。
    private static float _reloadGraceUntil = 0f;
    [HarmonyPatch(typeof(LobbyManager), "ReloadScene")]
    [HarmonyPrefix]
    static void OnReloadSceneStart() {
        try {
                _reloadGraceUntil = Time.unscaledTime + 20f;
        } catch (Exception __ex) { SRApi.Guard.Log("重载宽限起点", __ex); }
    }
    [HarmonyPatch(typeof(LobbyManager), "DisconnectBrokenClients")]
    [HarmonyPrefix]
    static bool SkipDisconnectDuringReload() {
        try {
                return Time.unscaledTime >= _reloadGraceUntil;
        } catch (Exception __ex) { SRApi.Guard.Log("重载期跳过断连检查", __ex); return true; }
    }

    // 保分：重载前备份各玩家分块，重载后房主按类型逐个重放 PointAwarded；playerTotal 不恢复。
    private static bool _preserveScoresOnReload = false;
    // 存类型快照而非 PointBlock 引用：tally 后对象会被复用，引用会读到改写后的 type。
    private static Dictionary<int, List<PointBlock.pointBlockType>> _scoreBackupBlocks = null;
    // 补分重放后置位：让 ClearNewPointBlocks 跳过清除，保证重放分块正常显示与 tally。
    private static bool _refillPending = false;
    // 补分标志置位时刻（-1 = 未置位），供 ClearNewPointBlocks 前缀做自愈。
    private static float _refillAt = -1f;
    // 外部加分（AlwaysAward）的分块键：被清除时缓存，重载备份时补回。
    private static HashSet<int> _externAwardedKeys = new HashSet<int>();
    // 结算时被清除的外部加分分块（值快照），重载备份时合并进 _scoreBackupBlocks。
    private static List<KeyValuePair<int, PointBlock.pointBlockType>> _clearedExternBlocks
        = new List<KeyValuePair<int, PointBlock.pointBlockType>>();   // 值快照
    // networkNumber -> handicap，重载后由房主显式写回 SyncVar
    private static Dictionary<int, int> _handicapBackup = null;

    // 保分标志有效期：标志正常由 ScoreKeeper.Setup 消费；超时视为失效，否则陈旧备份再补一遍会翻倍。
    private static float _preserveMarkedAt = -1f;
    private const float PreserveStaleAfter = 90f;

    // 重载前置保分标志（允许补分）。
    }
}
