// Level 的 partial 分片（只做代码组织，无逻辑改动）
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
        private static bool _reloadBusy; // 重载关卡进行中，防重复触发

        private static float _reloadBusyAt = -1f; // 加锁时刻，用于识别协程被销毁导致的锁残留

        private static void SetReloadBusy(bool busy)
        {
            _reloadBusy = busy;
            _reloadBusyAt = busy ? Time.unscaledTime : -1f;
        }

        // 锁残留自愈：协程被销毁时无人复位，超过 45 秒仍锁着即视为残留。
        private static bool ReloadLockStuck()
        {
            // 45s：与契约层 SrTimeouts.ReloadLockStaleSeconds 同源（预设页的重载锁用同一个值）
        return _reloadBusy && _reloadBusyAt >= 0f && Time.unscaledTime - _reloadBusyAt > SrTimeouts.ReloadLockStaleSeconds;
        }

        // 两个入口共用的防重入判定：重载中提示并拦下，锁残留则放锁放行本次操作。
        private static bool ReloadBlocked()
        {
            // 先查对方：自己准备动手时，布局预设正在加载就必须拦下（对方忙时自己也要等）。
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

        private static float _lastSnapshotAt = -999f; // 上次广播快照的时刻（冷却用）

        private static float _reloadLoadedAt = -1f; // 重载场景加载完成（FadeOut）时刻，用于确定何时可清 levelPortalXml

        private static float _reloadStartedAt = -1f; // 发起 ReloadScene 的时刻（超时兜底）

        public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

        // 仅派对 / 创意 / 自由建造关卡内生效（统一走 SRApi.GateModeAllows）。
        private static bool IsPartyCreativeOrFreeplay()
        {
            // 统一模式门控，IgnoreModeLimit 豁免已内置
            return SRApi.GateModeAllows(SRApi.ModeMask.Party | SRApi.ModeMask.Creative | SRApi.ModeMask.Freeplay);
        }

        // 真正重载当前关卡场景：重载前把房主快照写入 QuickSaver.levelPortalXml（static），重载后由房主 OnSetupStartLevel 读取并原生广播，全员重建方块。
        // 仅房主可调用（ReloadScene 是服务器操作），仅派对 / 创意 / 自由建造关卡内生效。
        public static void ReloadLevel()
        {
            ReloadLevelImpl(SRApi.T("重载关卡", "Reload Level"));
        }

        // 重载关卡实现：模式限制 + 快照写入 levelPortalXml。
        private static void ReloadLevelImpl(string feature)
        {
            // 启动成功后 _reloadBusy 由协程末尾复位；此处提前放锁会让连点并发重载。
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
                // 房主判定走统一门控 GateHost（= IsHost || OverrideHost）
                if (!SRApi.GateHost)
                {
                    SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
                    return;
                }
                // 必须服务器权威：GateHost 会放行开了无视房主限制的房客，但 NetworkServer.SendToAll 与 ReloadScene / CompressAndSendSnapshotBytes 在客户端都不执行，会假成功。
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
                if (ReloadBlocked()) return; // 含锁残留自愈
                // 快照刚广播过（3 秒内）先别重载，等方块重建落定
                if (Time.unscaledTime - _lastSnapshotAt < 3f) {
                    SRApi.Notify(SRApi.T("快照刚广播，请稍后重载", "Snapshot just broadcast; retry later"));
                    return;
                }
                SetReloadBusy(true);
                // 协程只在末尾复位 _reloadBusy；本方法所有提前 return 都在 StartCoroutine 之前，必须逐条手动复位，否则与广播快照共用的这把锁会永久锁死。
                // 生成房主当前快照并写入 QuickSaver.levelPortalXml（static），重载后由 OnSetupStartLevel 读取并原生广播全员。
                System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
                if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
                {
                    SetReloadBusy(false); // 失败路径复位
                    SRApi.Notify(SRApi.T("当前场景没有可同步的方块", "No blocks to sync in the current scene"));
                    return;
                }
                try
                {
                    QuickSaver.levelPortalXml = doc.OuterXml; // public static 字段，直接写
                }
                catch
                {
                    SetReloadBusy(false); // 失败路径复位
                    SRApi.Notify(SRApi.T("快照写入失败", "Failed to write the snapshot"));
                    return;
                }
                // KeepScore 的补分标志在协程里、真正调用 ReloadScene 之前才置位：
                // 提前置位时，后面的“游戏加载中/启动协程失败”等提前返回会让标志残留，下一次正常结算会错误补分。
                // GameSettings 取不到必须中止重载并解锁：客户端收不到 PrepareToReloadScene 会与房主脱同步。
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
                // 成败提示在协程内真正执行完才发出；模式随参数传入，协程内不再取 GameSettings（未判空会 NRE）。
                lm.StartCoroutine(ReloadSceneRoutine(feature, gsForMode.GameMode));
                coroutineStarted = true; // 复位交给协程末尾
            }
            catch (Exception ex)
            {
                // 仅协程未启动（StartCoroutine 之前的异常）时手动复位，否则锁残留
                if (!coroutineStarted) SetReloadBusy(false);
                SRApi.LogWarn((object)(feature + "失败: " + ex.Message));
            }
        }

        private static IEnumerator ReloadSceneRoutine(string feature, GameState.GameMode toMode)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame(); // 等加载画面 FadeIn 生效
            bool ok = false;
            try
            {
                LobbyManager lm = LobbyManager.instance;
                if ((UnityEngine.Object)(object)lm != (UnityEngine.Object)null)
                {
                    // 与游戏原生重载一致：先重置方块 ID 序列，否则重载后新 ID 会与快照里的 parentID/mainID 错位。
                    try { Placeable.SetInitialSequenceID(0); } catch (Exception __ex) { SRApi.Guard.Log("重载前重置方块 ID 序列", __ex); }
                    // 真正重载前才置补分标志：此后的失败只会让本次重载失败，不会把标志留给下一局。
                    if (ReloadKeepsScore) MarkPreserveScores();
                    lm.ReloadScene(toMode);
                    ok = true;
                }
            }
            catch (Exception ex)
            {
                SRApi.LogWarn((object)("重载场景失败: " + ex.Message));
            }
            // D-P1-7：收尾（清 levelPortalXml + 放 busy 锁）必须走 finally。
            // 以前 Notify / ClearPortalXml 任何一个抛异常，SetReloadBusy(false) 就永远执行不到，
            // 这把与"广播快照"共用的锁会一直锁着，直到 45 秒自愈或用户重开面板。
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
                _reloadLoadedAt = -1f; // 等待 LoadingInterstitialSplash.FadeOut 置位
                // 新场景加载完成后再等 2 秒才清 levelPortalXml 并复位 busy；FadeOut 不触发时用 25 秒硬上限兜底。
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

        // 重载场景加载完成（FadeOut，由 SR 重载钩子.OnLoadEnd 调用）：记录时刻供 ReloadSceneRoutine 决定何时清 levelPortalXml。
        public static void OnReloadSceneLoaded()
        {
            _reloadLoadedAt = Time.unscaledTime;
        }

        // 清掉写入的 QuickSaver.levelPortalXml（static）
        private static void ClearPortalXml()
        {
            try
            {
                QuickSaver.levelPortalXml = null;
            }
            catch (Exception __ex) { SRApi.Guard.Log("Level.Field", __ex); }
        }

        // 广播方块快照：房主重发当前关卡快照，全员按房主视角重建方块。
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

    // 重载后保留分数：重载前备份各玩家的分块列表，重载后房主按原类型逐个重放 PointAwarded，全员下回合 tally 恢复。
    // 不恢复 playerTotal（ScoreKeeper.Setup() 会清空 playerTotal 与分块）。
    private static bool _preserveScoresOnReload = false;
    // 备份：networkNumber -> 分块列表（含类型）
    // 值是分块类型的**快照**（不是 PointBlock 引用）：PointBlock 在 tally 后可能被复用/回收，
    // 持引用补分时会读到被改写的 type（audit P0-11）。
    private static Dictionary<int, List<PointBlock.pointBlockType>> _scoreBackupBlocks = null;
    // 补分重放后置位：下次结算的 ClearNewPointBlocks 跳过清除，保证重放分块正常显示与 tally。
    private static bool _refillPending = false;
    // 补分标志的置位时刻（-1 = 未置位）：给 ClearNewPointBlocks 前缀做墙钟自愈用（audit P0-7）。
    private static float _refillAt = -1f;
    // 外部加分（MsgPointAwarded.AlwaysAward=true）的分块标记：被清除时缓存，重载备份时补回。
    private static HashSet<int> _externAwardedKeys = new HashSet<int>();
    // 结算时被 Clear 的外部加分分块，重载备份时合并进 _scoreBackupBlocks
    // 存「玩家号 + 分块类型」的值快照，而不是 PointBlock 引用：游戏 tally 后这些对象会被复用，
    private static List<KeyValuePair<int, PointBlock.pointBlockType>> _clearedExternBlocks
        = new List<KeyValuePair<int, PointBlock.pointBlockType>>();   // 值快照：对象会被游戏复用
    // 备份：networkNumber -> handicap，重载后由房主显式写回 SyncVar
    private static Dictionary<int, int> _handicapBackup = null;

    // 保分标志的有效期：标志正常由 ScoreKeeper.Setup 消费，重载失败时会一直挂着，之后普通开局会被误当重载保分。
    // 超时（PreserveStaleAfter）即视为失效，否则会把陈旧备份分块再补一遍导致分数翻倍。
    private static float _preserveMarkedAt = -1f;
    private const float PreserveStaleAfter = 90f;

    // 重载前置保分标志（模式一：允许补分）
    }
}
