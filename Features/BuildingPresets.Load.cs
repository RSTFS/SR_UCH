// 布局预设 · 加载执行（BuildingPresets 分部）：房主写 levelPortalXml 并复刻原生重载流程、
// 树屋流程 / 直接替换场景、返回树屋、重载互斥锁。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {

    public partial class BuildingPresets {

        // ---------- 加载（房主）：写 levelPortalXml + 复刻原生重载流程 ----------

        // 为什么没有直接调 Level.ReloadLevel()：
        //   1) Level.ReloadLevel() 内部会重新调用 qs.GetCurrentXmlSnapshot() 并覆盖 QuickSaver.levelPortalXml，
        //      无法加载"不是当前布局"的预设；它也没有接受外部 XML 的公开入口。
        //   2) 拆分构建下 Level 编在独立的 SR_UCH_Level.dll，本文件只引用核心 SR_UCH.dll 与游戏程序集，
        //      按契约不能引用 Level 类型（否则拆分构建缺引用直接编译失败）。
        // 因此这里按 Level.ReloadLevelImpl / ReloadSceneRoutine 的同一套原生步骤复刻：
        //   写 QuickSaver.levelPortalXml -> 重置方块 ID 序列 -> 广播 PrepareToReloadScene -> 加载画面 FadeIn
        //   -> LobbyManager.ReloadScene(模式) -> 等游戏在 OnSetupStartLevel 消费 levelPortalXml。
        // 坑位与 Level 保持一致：失败必须清 levelPortalXml、锁 45 秒自愈、25 秒未消费兜底清 XML。
        private static void LoadPreset(PresetInfo p) {
            if (p == null) return;
            try {
                if (!ActionAllowed()) return;
                // GateHost 会放行开了"无视房主限制"的房客，但 WriteSyncVar / ReloadScene 在客户端不执行，会假成功。
                if (!SRApi.GateHost || !NetworkServer.active) {
                    SRApi.NotifyT("仅房主可加载预设：", "Host only: ", p.Name);
                    return;
                }
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) {
                    SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
                    return;
                }
                GameControl gc = lm.CurrentGameController as GameControl;
                if (gc == null) {
                    // 树屋/大厅（没有当前对局）：走游戏原生开局流程，进入预设关卡后应用方块。
                    if (lm.CurrentLevelSelectController != null) {
                        TryLoadPresetInTreehouse(p);
                        return;
                    }
                    SRApi.Notify(SRApi.T("未进入有效对局或树屋", "Not in an active match or treehouse"));
                    return;
                }
                if (gc.GetComponent<QuickSaver>() == null) {
                    SRApi.Notify(SRApi.T("当前对局不支持加载预设", "Loading presets is not supported in this match"));
                    return;
                }
                GameState gs = GameState.GetInstance();
                GameSettings st = GameSettings.GetInstance();
                if (gs == null || st == null) {
                    SRApi.Notify(SRApi.T("游戏状态尚未就绪，请稍后再试", "Game state not ready; try again later"));
                    return;
                }
                if (ReloadBlocked()) return;
                string xml = ReadPresetXml(p);
                if (string.IsNullOrEmpty(xml)) {
                    SRApi.Notify(SRApi.T("读取预设文件失败", "Failed to read the preset file"));
                    return;
                }
                XmlDocument doc = null;
                try {
                    doc = ParseXmlSafe(xml);
                } catch (Exception __ex) {
                    SRApi.Guard.Log("布局预设/解析预设文件", __ex);
                }
                if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
                    SRApi.Notify(SRApi.T("预设文件已损坏（根节点不是 scene）", "The preset file is damaged (root node is not scene)"));
                    return;
                }
                string presetScene = "";
                string activeScene = "";
                try { presetScene = doc.DocumentElement.GetAttribute("levelSceneName"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (string.IsNullOrEmpty(presetScene)) presetScene = p.Level ?? "";
                try { activeScene = SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                bool levelMismatch = !string.IsNullOrEmpty(presetScene) && !string.IsNullOrEmpty(activeScene)
                    && !string.Equals(presetScene, activeScene, StringComparison.Ordinal);
                // 自定义关卡：场景名都是 BlankLevel，要靠快照名/代码区分是不是同一个自定义关卡。
                bool customPreset = string.Equals(presetScene, "BlankLevel", StringComparison.OrdinalIgnoreCase);
                bool customIdentityDiff = customPreset && !SameCustomLevel(p, gs);
                if (customIdentityDiff) levelMismatch = true;
                if (levelMismatch) {
                    // 跨关卡加载 = 替换关卡：内置关卡走树屋 LaunchLevel；自定义关卡（BlankLevel）直接替换进 BlankLevel 场景并带上快照信息。
                    GameState.LevelName targetLevel;
                    if (customPreset) {
                        targetLevel = GameState.LevelName.BLANKLEVEL;
                    } else if (!TrySceneToLevel(presetScene, out targetLevel) || targetLevel == GameState.LevelName.BLANKLEVEL) {
                        SRApi.Notify(SRApi.T("无法识别预设所属关卡，暂不支持自动替换关卡", "The preset's level could not be recognized; automatic level replacement is not supported"));
                        return;
                    }
                    GameState.GameMode targetMode = ResolveLoadMode(p, st.GameMode);
                    bool skipTreehouse = _skipTreehouse != null && _skipTreehouse.Value;
                    bool hasRemoteClients = HasRemoteClients();
                    // 单人：始终直接替换（没有房客，无需回树屋）。
                    // 联机：只有勾选「跳过树屋流程」才直接替换，否则回树屋走游戏的 RpcSetNextLevel 同步。
                    if (hasRemoteClients && skipTreehouse) {
                        SRApi.LogWarn("[布局预设] 联机直接替换（已勾选跳过树屋）：房客 SelectedLevel 可能不同步");
                    }
                    if (!ConfirmOrArm("replace:" + p.Key)) {
                        _status = SRApi.T("将替换关卡：", "Level will be replaced: ") + LevelDisplayNameForScene(activeScene)
                            + SRApi.T(" → ", " -> ") + LevelDisplayNameForScene(presetScene)
                            + SRApi.T("；目标模式：", "; target mode: ") + ModeText(targetMode.ToString())
                            + SRApi.T("；5 秒内再点一次「确认」执行",
                                "; click Confirm again within 5 seconds");
                        SRApi.Notify(_status);
                        return;
                    }
                    ClearPending();
                    // P0-8：锁 / levelPortalXml / 失败兜底收敛到 ReloadGuard —— 不论是正常 return、
                    // 提前 return 还是抛异常，finally 里都会把「没交给协程」的那把锁放掉。
                    ReloadGuard guard = ReloadGuard.Begin("直接替换关卡");
                    try {
                        // 单人/无远程客户端：直接在当前对局内切场景。
                        // 否则退回树屋方案，因为游戏没有在“对局内”更新房客 SelectedLevel 的 RPC/消息。
                        bool __directOk = (!hasRemoteClients || skipTreehouse) && StartDirectReplace(p, xml, targetLevel, targetMode);
                        if (__directOk && hasRemoteClients) {
                            // 直接替换时游戏不会替我们同步房客的 SelectedLevel；借游戏自己的
                            // ClientRpc（RpcSetNextLevel）把下一关推给房客（audit 12-P0-3）。
                            try { SyncGuestsNextLevel(targetLevel, targetMode, p); }
                            catch (Exception __sg) { SRApi.Guard.Log("布局预设/同步房客下一关", __sg); }
                        }
                        if (__directOk) {
                            guard.Handoff();
                            SRApi.LogInfo("[布局预设] 直接替换关卡并加载预设：" + p.Name
                                + " → " + (customPreset ? (string.IsNullOrEmpty(p.LevelName) ? SRApi.T("自定义关卡", "Custom level") : p.LevelName) : LevelDisplayNameForScene(presetScene))
                                + (skipTreehouse && hasRemoteClients ? SRApi.T("（已跳过树屋流程，房客关卡状态可能不同步）", " (treehouse flow skipped; clients' level state may be out of sync)") : ""));
                        } else if (customPreset) {
                            // 失败：不动 guard（finally 会清 XML + 放锁）。
                            SRApi.Notify(SRApi.T("替换自定义关卡失败（详见日志）", "Failed to replace into the custom level (see the log)"));
                        } else {
                            lm.StartCoroutine(ReplaceLevelRoutine(p.Name, xml, presetScene, targetLevel, targetMode));
                            guard.Handoff();
                            SRApi.LogInfo("[布局预设] 正在替换关卡并加载预设：" + p.Name
                                + SRApi.T("（会先回树屋再进入 ", " (returns to the treehouse first, then enters ") + LevelDisplayNameForScene(presetScene) + "）");
                        }
                    } catch (Exception __rx) {
                        SRApi.Guard.Log("布局预设/开始替换关卡", __rx);
                        SRApi.Notify(SRApi.T("加载预设失败：无法开始替换关卡", "Failed to load the preset: could not start the level replacement"));
                    } finally {
                        guard.Settle();   // 没交给协程 -> 清 XML + 放锁；已交给协程 -> 什么都不做
                    }
                    return;
                }
                ClearPending();
                ReloadGuard reloadGuard = ReloadGuard.Begin("重载加载预设");
                try {
                    // 一次性静态字段：游戏在 OnSetupStartLevel 里读取后立即置 null 并原生广播给全员。
                    SetLevelPortalXml(xml);
                    // 不再校验/拦截模式差异：按预设自己的模式（或页面选择的加载模式）走原来的重载流程（ReloadScene 会把模式同步给房客）。
                    GameState.GameMode toMode = ResolveLoadMode(p, st.GameMode);
                    try { st.GameMode = toMode; } catch (Exception __mx) { SRApi.Guard.Log("布局预设/设置预设模式", __mx); }
                    MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene {
                        reloadToMode = toMode,
                        snapshotInfo = gs.currentSnapshotInfo
                    };
                    NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
                    if (LoadingInterstitialSplash.Instance != null) {
                        LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
                        LoadingInterstitialSplash.Instance.FadeIn();
                    }
                    // 成败提示在协程内真正执行完才发出；锁由协程末尾复位。
                    lm.StartCoroutine(LoadPresetRoutine(p.Name, xml, toMode));
                    reloadGuard.Handoff();
                    SRApi.LogInfo("[布局预设] " + SRApi.T("加载预设：", "Loading preset: ") + p.Name
                        + "（" + LevelText(p) + "）"
                        + SRApi.T("，全员重载中（分数会重置）", "; everyone is reloading (scores will reset)"));
                } catch (Exception __ex) {
                    SRApi.Guard.Log("布局预设/开始加载", __ex);
                    SRApi.Notify(SRApi.T("加载预设失败", "Failed to load the preset"));
                } finally {
                    reloadGuard.Settle();
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/加载", __ex);
            }
        }

        // 返回树屋：走游戏原生「结束对局」流程（与替换关卡的第一步相同），房主有效。
        private static void ReturnToTreehouse() {
            try {
                if (!SRApi.GateHost || !NetworkServer.active) {
                    SRApi.NotifyT("仅房主可返回树屋：", "Host only: ", SRApi.T("返回树屋", "Back to treehouse"));
                    return;
                }
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = lm != null ? lm.CurrentGameController as GameControl : null;
                if (gc == null) { SRApi.Notify(SRApi.T("当前不在对局中，无需返回树屋", "Not in a match; nothing to return from")); return; }
                SetReloadBusy(true, "返回树屋");
                gc.EndGame();
                // EndGame 会切到树屋场景；锁必须由 watchdog 释放，否则后续所有加载都被"重载进行中"拦住。
                try { lm.StartCoroutine(TreehouseReturnWatchdog()); }
                catch (Exception __wx) { SRApi.Guard.Log("布局预设/返回树屋watchdog", __wx); SetReloadBusy(false); }
                SRApi.Notify(SRApi.T("正在返回树屋…", "Returning to the treehouse…"));
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/返回树屋", __ex);
                SetReloadBusy(false);
                SRApi.Notify(SRApi.T("返回树屋失败", "Failed to return to the treehouse"));
            }
        }

        // 返回树屋 watchdog：等树屋/大厅就绪或超时后释放重载锁。
        private static IEnumerator TreehouseReturnWatchdog() {
            float deadline = Time.unscaledTime + PresetConsumeTimeout;
            while (Time.unscaledTime < deadline) {
                try {
                    LobbyManager cur = LobbyManager.instance;
                    if (cur != null && cur.CurrentLevelSelectController != null
                        && SceneManager.GetActiveScene().name == "TreeHouseLobby") break;
                } catch (Exception __ex) { SRApi.Guard.Log("布局预设/返回树屋watchdog", __ex); }
                yield return null;
            }
            SetReloadBusy(false);
        }

        // 预设目录名（GameState.GameMode 的 ToString）-> 游戏模式枚举；IMPORTED/UNKNOWN 等非具体模式返回 false。
        private static bool TryParseMode(string mode, out GameState.GameMode gm) {
            gm = GameState.GameMode.FREEPLAY;
            try {
                if (string.IsNullOrEmpty(mode)) return false;
                if (!Enum.TryParse<GameState.GameMode>(mode.Trim(), true, out gm)) return false;
                return Enum.IsDefined(typeof(GameState.GameMode), gm);
            } catch { return false; }
        }

        // 计算本次加载实际使用的模式：
        //   1) 默认（_loadMode 为空）：跟随预设自身保存的模式（p.Mode），解析失败时保留调用方传入的当前模式；
        //   2) 选择了具体「加载模式」：强制覆盖预设模式，所有加载/重载路径都以它为准。
        private static GameState.GameMode ResolveLoadMode(PresetInfo p, GameState.GameMode fallback) {
            GameState.GameMode mode = fallback;
            try {
                GameState.GameMode presetMode;
                if (p != null && IsConcreteMode(p.Mode) && TryParseMode(p.Mode, out presetMode)) mode = presetMode;
                GameState.GameMode forcedMode;
                if (TryParseMode(_loadMode, out forcedMode)) {
                    if (forcedMode != mode) {
                        // 页面选了「加载模式」就覆盖预设自身模式：这是预期行为，但必须留痕（audit A-7）。
                        SRApi.LogInfo("[布局预设] 加载模式覆盖：预设=" + mode + " → 选择=" + forcedMode
                            + "（预设 " + (p != null ? (p.Name ?? "") : "") + "）");
                    }
                    mode = forcedMode;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/解析加载模式", __ex);
            }
            return mode;
        }

        // 是否还有远程客户端（房主自己不算）：只有单人/无远程玩家时才允许“不经树屋”的直接切关卡，
        // 因为游戏没有在对局内给房客同步 GameState.SelectedLevel 的 RPC/消息（那套 RpcSetNextLevel 在树屋的
        // LevelSelectController 上，对局场景里没有该对象）。
        private static bool HasRemoteClients() {
            try {
                if (!NetworkServer.active) return false;
                if (NetworkServer.connections == null) return true;
                // UNET 的 connections 按连接 ID 索引，断开的客户端会留下 null 槽（游戏自己的循环也都判 null），
                // 所以不能直接看 Count：单人局也可能 > 1 → 误判联机后错误拦截/放行。
                int remote = 0;
                for (int i = 0; i < NetworkServer.connections.Count; i++) {
                    UnityEngine.Networking.NetworkConnection c = NetworkServer.connections[i];
                    if (c == null) continue;
                    try {
                        if (!c.isConnected) continue;
                        if (c.connectionId == 0) continue;   // 0 = 本机（房主自己）
                    } catch { continue; }
                    remote++;
                }
                return remote > 0;
            } catch { return true; }
        }

        // 不经树屋的直接替换（仅单人/无远程客户端）：在当前对局内设置 SelectedLevel / 模式 / 快照信息，
        // 写 QuickSaver.levelPortalXml，再走原生 LobbyManager.ReloadScene（内部 ServerChangeScene 到 SelectedLevel）。
        // 调用链：GameState.SelectedLevel=target → GameSettings.GameMode=targetMode → currentSnapshotInfo →
        //         QuickSaver.levelPortalXml=xml → 广播 PrepareToReloadScene → FadeIn → ReloadScene(targetMode)
        //         → 新场景 OnSetupStartLevel 应用预设。房客端 SelectedLevel 无法更新，所以有远程客户端时不用这条路。
        private static bool StartDirectReplace(PresetInfo p, string xml, GameState.LevelName targetLevel, GameState.GameMode targetMode) {
            try {
                LobbyManager lm = LobbyManager.instance;
                GameState gs = GameState.GetInstance();
                GameSettings st = GameSettings.GetInstance();
                if (lm == null || gs == null || st == null || !NetworkServer.active) return false;
                gs.SelectedLevel = targetLevel;
                LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
                info.nextLevel = targetLevel;
                if (p != null) {
                    info.snapshotName = p.LevelName;
                    info.snapshotCode = p.LevelCode;
                }
                // 旧索引可能没有名字/代码：从预设 XML 根属性补齐（自定义关卡必须带上，否则关卡显示空白）。
                if (string.IsNullOrEmpty(info.snapshotName) || string.IsNullOrEmpty(info.snapshotCode)) {
                    try {
                        XmlDocument __xd = ParseXmlSafe(xml);
                        XmlElement __root = __xd.DocumentElement;
                        if (__root != null) {
                            if (string.IsNullOrEmpty(info.snapshotName)) info.snapshotName = __root.GetAttribute("snapshotName");
                            if (string.IsNullOrEmpty(info.snapshotCode)) info.snapshotCode = __root.GetAttribute("snapshotCode");
                            if (string.IsNullOrEmpty(info.snapshotCode)) info.snapshotCode = __root.GetAttribute("levelCode");
                        }
                    } catch (Exception __xe) { SRApi.Guard.Log("布局预设/解析快照名", __xe); }
                }
                gs.currentSnapshotInfo = info;
                st.GameMode = targetMode;
                SetLevelPortalXml(xml);
                MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene {
                    reloadToMode = targetMode,
                    snapshotInfo = info
                };
                NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
                if (LoadingInterstitialSplash.Instance != null) {
                    LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
                    LoadingInterstitialSplash.Instance.FadeIn();
                }
                lm.StartCoroutine(LoadPresetRoutine(p != null ? p.Name : "", xml, targetMode));
                SRApi.LogInfo("[布局预设] 直接替换关卡（无远程客户端）: " + LevelDisplayNameForScene(SceneManager.GetActiveScene().name)
                    + " -> " + LevelDisplayNameForScene(GameState.GetLevelSceneName(targetLevel)));
                return true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/直接替换关卡", __ex);
                try { QuickSaver.levelPortalXml = null; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                return false;
            }
        }

        // 替换关卡加载协程：先用 GameControl.EndGame() 走游戏原生「回树屋」流程（房主会向房客 RPC 一起回），
        // 等 TreeHouseLobby + LevelSelectController 就绪后，用与树屋加载相同的原生 LaunchLevel(portal) 进入预设关卡，
        // 再覆盖 QuickSaver.levelPortalXml 为预设 XML，让新场景 OnSetupStartLevel 应用方块。
        private static IEnumerator ReplaceLevelRoutine(string name, string xml, string presetScene,
            GameState.LevelName targetLevel, GameState.GameMode targetMode) {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            bool ok = false;
            if (StartReturnToTreehouse(targetMode)) {
                float deadline = Time.unscaledTime + 20f;
                LevelSelectController lsc = null;
                while (Time.unscaledTime < deadline) {
                    LobbyManager cur = LobbyManager.instance;
                    lsc = cur != null ? cur.CurrentLevelSelectController : null;
                    if (lsc != null && SceneManager.GetActiveScene().name == "TreeHouseLobby") break;
                    yield return null;
                }
                if (lsc == null) {
                    SRApi.Notify(SRApi.T("替换关卡失败：等待回到树屋超时", "Level replacement failed: timed out waiting to return to the treehouse"));
                } else {
                    // 等树屋门/大厅状态初始化（场景刚加载完的一两帧门可能还没激活）。
                    yield return new WaitForSeconds(0.8f);
                    ok = EnterPresetLevelFromTreehouse(lsc, name, xml, presetScene, targetLevel);
                }
            }
            if (!ok) {
                try { QuickSaver.levelPortalXml = null; } catch (Exception __cx) { SRApi.Guard.Log("布局预设/清理替换关卡XML", __cx); }
                SetReloadBusy(false);
            }
        }

        // 替换关卡第一步（无 yield，避免 C# 不允许在 try/catch 里 yield）：原生 EndGame 回树屋。
        private static bool StartReturnToTreehouse(GameState.GameMode targetMode) {
            try {
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = lm != null ? lm.CurrentGameController as GameControl : null;
                if (lm == null || gc == null || !NetworkServer.active) {
                    SRApi.Notify(SRApi.T("无法替换关卡：当前没有可结束的对局", "Cannot replace the level: there is no active match"));
                    return false;
                }
                // 目标模式先在本机设好：LaunchLevel 内部会 CallRpcSetGameMode 同步给房客。
                try { GameSettings st = GameSettings.GetInstance(); if (st != null) st.GameMode = targetMode; } catch (Exception __mx) { SRApi.Guard.Log("布局预设/替换关卡设置模式", __mx); }
                // 原生「回树屋」：RPC 全员淡出到 TreeHouseLobby，并清理当前对局玩家。
                gc.EndGame();
                return true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/结束对局回树屋", __ex);
                SRApi.Notify(SRApi.T("替换关卡失败：无法结束当前对局", "Level replacement failed: could not end the current match"));
                return false;
            }
        }

        // 替换关卡第二步（无 yield）：树屋已就绪，找门并走原生 LaunchLevel 进预设关卡，再注入预设 XML。
        private static bool EnterPresetLevelFromTreehouse(LevelSelectController lsc, string name, string xml,
            string presetScene, GameState.LevelName targetLevel) {
            try {
                LevelPortal portal = FindTreehousePortal(lsc, targetLevel);
                if (portal == null) {
                    SRApi.Notify(SRApi.T("替换关卡失败：树屋里找不到「", "Level replacement failed: no treehouse portal for \"")
                        + LevelDisplayNameForScene(presetScene) + SRApi.T("」的入口（可能未解锁）", "\" (possibly locked)"));
                    return false;
                }
                if (!AllLobbyPlayersPickedCharacter(LobbyManager.instance)) {
                    SRApi.Notify(SRApi.T("已回到树屋：有玩家还没选角色，先选好角色后再点一次「加载」即可进入预设关卡",
                        "Returned to the treehouse: some players have not picked a character; pick one and click Load again to enter the preset level"));
                    return false;
                }
                try {
                    if (LoadingInterstitialSplash.Instance != null) {
                        LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
                        LoadingInterstitialSplash.Instance.FadeIn();
                    }
                } catch (Exception __fx) { SRApi.Guard.Log("布局预设/替换关卡加载画面", __fx); }
                lsc.LaunchLevel(portal);
                try { QuickSaver.levelPortalXml = xml; } catch (Exception __qx) { SRApi.Guard.Log("布局预设/替换关卡写入预设XML", __qx); }
                try {
                    GameState gs = GameState.GetInstance();
                    if (gs != null) {
                        gs.SelectedLevel = targetLevel;
                        LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
                        info.nextLevel = targetLevel;
                        gs.currentSnapshotInfo = info;
                    }
                } catch (Exception __sx) { SRApi.Guard.Log("布局预设/替换关卡同步状态", __sx); }
                LobbyManager.instance.StartCoroutine(TreehouseLoadWatchdog(name, xml));
                SRApi.LogInfo("[布局预设] " + SRApi.T("正在替换关卡：进入 ", "Replacing level: entering ") + LevelDisplayNameForScene(presetScene)
                    + SRApi.T(" 并应用预设 ", " and applying preset ") + name);
                return true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/替换关卡进入", __ex);
                SRApi.Notify(SRApi.T("替换关卡加载失败（详见日志）", "Level replacement failed (see the log)"));
                return false;
            }
        }
        // 树屋加载调用链（复用游戏原生流程，避免自己拼网络消息）：
        //   1) 读预设 XML，取 levelSceneName → 反查 GameState.LevelName → 在树屋关卡门里找到对应 LevelPortal；
        //   2) 先 LoadingInterstitialSplash.FadeIn()：原生 LevelSelectController.FadeToLevel 要等 FadeOut 进入 SHOW，
        //      而房主本机不会收到 ClientRpc RpcSetNextLevel（那里才会 FadeIn），所以这一步必须自己触发；
        //   3) 调游戏原生 LevelSelectController.LaunchLevel(portal)：它会 CallRpcSetNextLevel 让房客同步、
        //      清理/准备大厅玩家，然后 FadeToLevel → LobbyManager.ServerChangeScene(关卡场景)；
        //   4) LaunchLevel 同步把 QuickSaver.levelPortalXml 设为 portal.snapshotXml（普通关卡为 null），
        //      调用返回后立刻用预设 XML 覆盖；FadeToLevel 还要等 FadeOut.SHOW + 0.2 秒 + UnloadUnusedAssets，
        //      所以覆盖一定发生在新场景 OnSetupStartLevel 读取 levelPortalXml 之前；
        //   5) 新场景 OnSetupStartLevel 读取 levelPortalXml → CompressAndSendSnapshotBytes 广播 + LoadXmlSnapshotFromString
        //      本地应用 →「切到预设关卡并应用方块」完成。仅房主可执行；自定义关卡（BlankLevel）无法用 LevelPortal 映射。
        private static void TryLoadPresetInTreehouse(PresetInfo p) {
            bool started = false;
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) {
                    SRApi.Notify(SRApi.T("当前不在树屋或对局中", "Not in a treehouse or match"));
                    return;
                }
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                if (lsc == null) {
                    SRApi.Notify(SRApi.T("当前不在树屋/大厅中，无法切换到预设关卡", "Not in the treehouse/lobby; cannot switch to the preset level"));
                    return;
                }
                if (!SRApi.GateHost || !NetworkServer.active) {
                    SRApi.NotifyT("仅房主可在树屋加载预设：", "Host only: ", p.Name);
                    return;
                }
                if (ReloadBlocked()) return;
                string xml = ReadPresetXml(p);
                if (string.IsNullOrEmpty(xml)) {
                    SRApi.Notify(SRApi.T("读取预设文件失败", "Failed to read the preset file"));
                    return;
                }
                XmlDocument doc = null;
                try {
                    doc = ParseXmlSafe(xml);
                } catch (Exception __ex) {
                    SRApi.Guard.Log("布局预设/树屋解析预设文件", __ex);
                }
                if (doc == null || doc.DocumentElement == null || doc.DocumentElement.Name != "scene") {
                    SRApi.Notify(SRApi.T("预设文件已损坏（根节点不是 scene）", "The preset file is damaged (root node is not scene)"));
                    return;
                }
                string presetScene = "";
                try { presetScene = doc.DocumentElement.GetAttribute("levelSceneName"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (string.IsNullOrEmpty(presetScene)) presetScene = p.Level ?? "";
                GameState.LevelName target;
                if (!TrySceneToLevel(presetScene, out target)) {
                    SRApi.Notify(SRApi.T("树屋加载仅支持游戏内置关卡；自定义关卡请先进入该关卡再加载预设", "Treehouse loading only supports built-in levels; enter custom levels first and load there"));
                    return;
                }
                if (target == GameState.LevelName.BLANKLEVEL) {
                    // 自定义关卡：树屋里没有可用的关卡门，直接替换进 BlankLevel 并带上预设的快照名/代码。
                    // 联机时必须勾选「跳过树屋流程」才允许直接替换（否则只提示，避免房客关卡状态不同步）。
                    bool skipTH = _skipTreehouse != null && _skipTreehouse.Value;
                    if (HasRemoteClients() && !skipTH) {
                        SRApi.Notify(SRApi.T("联机替换自定义关卡需要先勾选『跳过树屋流程』", "Replacing a custom level online requires enabling 'Skip treehouse' first"));
                        return;
                    }
                    if (!AllLobbyPlayersPickedCharacter(lm)) {
                        SRApi.Notify(SRApi.T("有玩家还没选角色：请等所有人选好角色后再加载（否则可能开局异常）", "Some players have not picked a character yet; wait until everyone has one"));
                        return;
                    }
                    GameState.GameMode __cm = ResolveLoadMode(p, GameSettings.GetInstance().GameMode);
                    if (StartDirectReplace(p, xml, GameState.LevelName.BLANKLEVEL, __cm)) {
                        SetReloadBusy(true);
                        started = true;
                        SRApi.LogInfo("[布局预设] 正直接从树屋进入自定义关卡并加载预设：" + p.Name);
                    } else {
                        SRApi.Notify(SRApi.T("进入自定义关卡失败（详见日志）", "Failed to enter the custom level (see the log)"));
                    }
                    return;
                }
                LevelPortal portal = FindTreehousePortal(lsc, target);
                if (portal == null) {
                    SRApi.Notify(SRApi.T("树屋里找不到「", "No treehouse portal found for \"") + LocalizedLevelName(presetScene)
                        + SRApi.T("」的入口（可能未解锁或不可用）", "\" (possibly locked or unavailable)"));
                    return;
                }
                if (!AllLobbyPlayersPickedCharacter(lm)) {
                    SRApi.Notify(SRApi.T("有玩家还没选角色：现在加载会把未选角色的玩家移出房间，请等所有人选好角色后再试", "Some players have not picked a character yet; loading now would remove them from the lobby. Wait until everyone has picked a character"));
                    return;
                }
                // 树屋普通关卡：LaunchLevel 会用 GameSettings.GameMode 并 CallRpcSetGameMode 同步给房客，
                // 因此进入前先按「默认预设模式 / 强制加载模式」设置，保证该路径同样受加载模式控制。
                try {
                    GameSettings __lst = GameSettings.GetInstance();
                    if (__lst != null) __lst.GameMode = ResolveLoadMode(p, __lst.GameMode);
                } catch (Exception __mex) { SRApi.Guard.Log("布局预设/树屋加载设置模式", __mex); }
                // 房主本机不会收到 ClientRpc RpcSetNextLevel，需要自己把加载画面推到 SHOW，否则原生 FadeToLevel 会一直等。
                try {
                    if (LoadingInterstitialSplash.Instance != null) {
                        LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
                        LoadingInterstitialSplash.Instance.FadeIn();
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("布局预设/树屋加载画面", __ex); }
                lsc.LaunchLevel(portal);
                // LaunchLevel 已把 levelPortalXml 写成 portal.snapshotXml（普通关卡为 null），立刻覆盖为预设 XML。
                try { QuickSaver.levelPortalXml = xml; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/树屋写入预设XML", __ex); }
                // 补齐房主本机的 SelectedLevel/currentSnapshotInfo（ClientRpc 只在房客端执行）。
                try {
                    GameState gs = GameState.GetInstance();
                    if (gs != null) {
                        gs.SelectedLevel = target;
                        LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
                        info.nextLevel = target;
                        gs.currentSnapshotInfo = info;
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("布局预设/树屋同步关卡状态", __ex); }
                SetReloadBusy(true);
                lm.StartCoroutine(TreehouseLoadWatchdog(p.Name, xml));
                started = true;
                SRApi.LogInfo("[布局预设] " + SRApi.T("加载预设并进入关卡：", "Loading preset into level: ") + p.Name
                    + SRApi.T(" → ", " -> ") + LocalizedLevelName(presetScene));
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/树屋加载", __ex);
                SRApi.Notify(SRApi.T("从树屋加载预设失败（详见日志）", "Failed to load the preset from the treehouse (see the log)"));
            }
            if (!started) {
                try { QuickSaver.levelPortalXml = null; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理树屋预设XML", __ex); }
                SetReloadBusy(false);
            }
        }

        // 在树屋里找目标关卡的门：优先 LevelSelectController.portals（场景实际摆放的门），只返回激活状态的门。
        private static LevelPortal FindTreehousePortal(LevelSelectController lsc, GameState.LevelName target) {
            try {
                if (lsc != null && lsc.portals != null) {
                    for (int i = 0; i < lsc.portals.Length; i++) {
                        LevelPortal p = lsc.portals[i];
                        if (p == null || p.TargetLevel != target) continue;
                        if (p.gameObject == null || !p.gameObject.activeInHierarchy) continue;
                        return p;
                    }
                }
                LevelPortal[] all = UnityEngine.Object.FindObjectsOfType<LevelPortal>();
                if (all != null) {
                    for (int i = 0; i < all.Length; i++) {
                        LevelPortal p = all[i];
                        if (p == null || p.TargetLevel != target) continue;
                        if (p.gameObject == null || !p.gameObject.activeInHierarchy) continue;
                        return p;
                    }
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/查找树屋关卡门", __ex);
            }
            return null;
        }

        // LaunchLevel 会移除还没选角色的玩家；先检查，避免用户误操作把人踢出房间。
        private static bool AllLobbyPlayersPickedCharacter(LobbyManager lm) {
            try {
                if (lm == null || lm.lobbySlots == null) return true;
                for (int i = 0; i < lm.lobbySlots.Length; i++) {
                    LobbyPlayer lp = lm.lobbySlots[i] as LobbyPlayer;
                    if (lp == null) continue;
                    if (lp.PickedAnimal == Character.Animals.NONE) return false;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/检查大厅玩家角色", __ex);
            }
            return true;
        }

        // 树屋加载后的保护：等游戏消费 levelPortalXml（新场景 OnSetupStartLevel 后置 null）再放锁；
        // 25 秒未消费则清掉本次写入的 XML，避免污染下一次原生开局。
        private static IEnumerator TreehouseLoadWatchdog(string name, string expectedXml) {
            float startedAt = Time.unscaledTime;
            // 三种结局必须分开（评审 P0-A1）：以前"变了"和"被取走"都算 consumed，
            // 于是别的预设/别的功能把字段覆盖掉时，会误判成"本预设已被游戏消费"。
            int outcome = 0;   // 0=超时 1=被游戏取走 2=被别的内容替换
            while (Time.unscaledTime - startedAt < PresetConsumeTimeout) {
                string remaining = null;
                try { remaining = QuickSaver.levelPortalXml; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取树屋预设XML", __ex); }
                if (string.IsNullOrEmpty(remaining)) { outcome = 1; break; }
                if (!ReferenceEquals(remaining, expectedXml) && remaining != expectedXml) { outcome = 2; break; }
                yield return null;
            }
            if (outcome == 0) {
                try {
                    if (QuickSaver.levelPortalXml == expectedXml) QuickSaver.levelPortalXml = null;
                } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理树屋超时预设XML", __ex); }
                SRApi.LogWarn("[布局预设] 树屋加载后预设 XML 未被游戏消费（可能开局失败），已清掉: " + name);
            } else if (outcome == 2) {
                // 别人写入了新内容：不要清它（那是别人的），但要说清"本次预设没有生效"。
                SRApi.LogWarn("[布局预设] 待消费的预设 XML 已被其它加载覆盖，本次预设可能没有生效: " + name);
            }
            SetReloadBusy(false);
        }

        private static IEnumerator LoadPresetRoutine(string name, string expectedXml, GameState.GameMode toMode) {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame(); // 等加载画面 FadeIn 生效（与 Level 的重载协程一致）
            bool ok = false;
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    // 与原生重载一致：Reset 方块 ID 序列必须紧挨着 ReloadScene，否则重载后新 ID 会与快照里的 parentID/mainID 错位。
                    try { Placeable.SetInitialSequenceID(0); } catch (Exception __ex) { SRApi.Guard.Log("布局预设/重置方块ID序列", __ex); }
                    lm.ReloadScene(toMode);
                    ok = true;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/重载场景", __ex);
            }
            if (!ok) {
                SRApi.Notify(SRApi.T("加载预设失败：重载场景失败", "Failed to load the preset: scene reload failed"));
                try { QuickSaver.levelPortalXml = null; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理预设XML", __ex); }
                SetReloadBusy(false);
                yield break;
            }
            // 游戏在 OnSetupStartLevel 消费 levelPortalXml 后立即置 null：等消费完成再放锁。
            // 25 秒还没被消费（场景没真正重建）就清掉本次写入的 XML，避免污染下一次原生重载。
            float started = Time.unscaledTime;
            bool consumed = false;
            while (Time.unscaledTime - started < PresetConsumeTimeout) {
                string remaining = null;
                try { remaining = QuickSaver.levelPortalXml; } catch (Exception __ex) { SRApi.Guard.Log("布局预设/读取预设XML", __ex); }
                if (string.IsNullOrEmpty(remaining) || remaining != expectedXml) {
                    consumed = true;
                    break;
                }
                yield return null;
            }
            if (!consumed) {
                try {
                    if (QuickSaver.levelPortalXml == expectedXml) QuickSaver.levelPortalXml = null;
                } catch (Exception __ex) { SRApi.Guard.Log("布局预设/清理超时预设XML", __ex); }
                SRApi.LogWarn("[布局预设] 预设 XML 未被游戏消费（可能重载未完成），已清掉，避免影响下一次原生重载: " + name);
                SRApi.Notify(SRApi.T("预设加载可能未生效：等待场景重载超时", "The preset may not have been applied: timed out waiting for the scene reload"));
            }
            SetReloadBusy(false);
        }

        private static bool ReloadBlocked() {
            try {
                // 先查对方：自己准备动手时，关卡重载正在跑就必须拦下（对方忙时自己也要等）。
                try {
                    Func<bool> other = Slots.Require<Func<bool>>("level.reloadBusy");
                    if (other != null && other()) {
                        // 对方锁有自己的自愈；如果它一直不放，至少让日志说明"卡在谁身上"（audit A-9）。
                        _otherBusySince = _otherBusySince < 0f ? Time.unscaledTime : _otherBusySince;
                        if (Time.unscaledTime - _otherBusySince > 60f) {
                            SRApi.LogWarn("[布局预设] 关卡重载锁已持续 " + (int)(Time.unscaledTime - _otherBusySince)
                                + " 秒未释放（level.reloadBusy=true），预设加载被拦住");
                            _otherBusySince = Time.unscaledTime;   // 每 60 秒提醒一次
                        }
                        SRApi.Notify(SRApi.T("关卡正在重载，请稍候", "Level reload in progress; please wait"));
                        return true;
                    }
                    _otherBusySince = -1f;
                } catch (Exception __ex) { SRApi.Guard.Log("预设/重载互斥检查", __ex); }
                if (!_reloadBusy) return false;
                if (_reloadBusyAt >= 0f && Time.unscaledTime - _reloadBusyAt > ReloadLockStale) {
                    SetReloadBusy(false);
                    SRApi.LogWarn("[布局预设] 检测到残留的忙碌锁（超过 45 秒未复位），已自动放锁");
                    return false;
                }
                SRApi.Notify(SRApi.T("重载进行中，请稍候", "Reload in progress"));
                return true;
            } catch { return false; }
        }

        // 重载锁的成对管理（audit P0-8）：Begin 加锁，
        //   交给协程 -> Handoff()（协程末尾自己放锁）；
        //   没交给协程 -> Settle()（清 levelPortalXml + 放锁）。
        // 调用点一律写在 try/finally 里：任何异常或提前 return 都不会留下悬着的锁。
        private sealed class ReloadGuard {
            private bool _handed;
            private ReloadGuard() { }
            public static ReloadGuard Begin(string why) {
                SetReloadBusy(true, why);
                return new ReloadGuard();
            }
            public void Handoff() { _handed = true; }
            public void Settle() {
                if (_handed) return;
                try { QuickSaver.levelPortalXml = null; }
                catch (Exception __cx) { SRApi.Guard.Log("布局预设/清理预设XML", __cx); }
                SetReloadBusy(false);
            }
        }

        // levelPortalXml 是游戏侧的 static 字段：写进去之后如果加载被掐断（换场景把挂在
        // LobbyManager 上的协程销毁），脏 XML 会一直留着，下一次原生开局会误加载旧快照
        // （audit 12-P0-1/12-P0-2）。这里记录写入时刻，由 tick 兜底清理，不依赖协程存活。
        private static float _xmlSetAt = -1f;
        private static string _xmlWritten;

        // 把"下一关"用游戏自己的 ClientRpc 同步给房客：RpcSetNextLevel 会写房客的
        // GameState.SelectedLevel / currentSnapshotInfo 并触发 FadeIn，正是原生换关的路径。
        private static void SyncGuestsNextLevel(GameState.LevelName level, GameState.GameMode mode, PresetInfo p) {
            try {
                LobbyManager lm = LobbyManager.instance;
                LevelSelectController lsc = lm != null ? lm.CurrentLevelSelectController : null;
                if (lsc == null) { SRApi.LogWarn("[布局预设] 没有 LevelSelectController，跳过房客同步"); return; }
                LevelSelectController.PlayedSnapshotInfo info = new LevelSelectController.PlayedSnapshotInfo();
                info.nextLevel = level;
                if (p != null) { info.snapshotName = p.LevelName; info.snapshotCode = p.LevelCode; }
                // 老引擎方法签名：CallRpcSetNextLevel 会广播到所有客户端。
                lsc.CallRpcSetNextLevel(info);
                SRApi.LogInfo("[布局预设] 已向房客广播下一关: " + level);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/广播下一关", __ex); }
        }

        private static void SetLevelPortalXml(string xml) {
            try {
                QuickSaver.levelPortalXml = xml;
                _xmlSetAt = Time.unscaledTime;
                _xmlWritten = xml;
            } catch (Exception __sx) { SRApi.Guard.Log("布局预设/写入 levelPortalXml", __sx); }
        }

        // 每帧兜底：超过 25 秒还没被游戏消费（字段仍是自己写的那份）就清掉。
        private static void TickXmlGuard() {
            try {
                if (_xmlSetAt < 0f) return;
                if (Time.unscaledTime - _xmlSetAt <= 25f) return;
                string cur = null;
                try { cur = QuickSaver.levelPortalXml; } catch (Exception __rc) { SRApi.Guard.Log("布局预设/读 levelPortalXml", __rc); }
                if (cur != null && ReferenceEquals(cur, _xmlWritten)) {
                    try { QuickSaver.levelPortalXml = null; } catch (Exception __cc) { SRApi.Guard.Log("布局预设/清 levelPortalXml", __cc); }
                    SRApi.LogWarn("[布局预设] levelPortalXml 25 秒未被消费，已清理，避免污染下一局");
                }
                _xmlSetAt = -1f;
                _xmlWritten = null;
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/XML 兜底清理", __ex); }
        }

        // 锁的单一入口：记录"谁加的锁"，方便自愈时在日志里指出漏放锁的路径（audit P0-8）。
        private static void SetReloadBusy(bool busy, string reason = null) {
            _reloadBusy = busy;
            _reloadBusyAt = busy ? Time.unscaledTime : -1f;
            _reloadBusyReason = busy ? (reason ?? "未标注") : null;
        }

        // 每帧自愈：以前只有"用户再点一次加载"才会发现残留锁；现在 tick 里就能释放并留日志。
        private static void TickReloadLock() {
            try {
                if (!_reloadBusy || _reloadBusyAt < 0f) return;
                if (Time.unscaledTime - _reloadBusyAt <= ReloadLockStale) return;
                SRApi.LogWarn("[布局预设] 重载锁超时自动释放（来源：" + (_reloadBusyReason ?? "未标注") + "）");
                SetReloadBusy(false);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/重载锁自愈", __ex); }
        }


    }
}
