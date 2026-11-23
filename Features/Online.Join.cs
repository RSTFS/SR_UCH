// Online 加入流程：房间码/直接加入、退出旧房后继续加入、在途与看门狗状态。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    public partial class Online {
        public static void JoinLobby(string id) { JoinLobby(id, false); }

        public static void JoinByCode(string code) {
            if (string.IsNullOrEmpty(code)) return;
            JoinLobby(code.Trim(), true);
        }

        private static string CodeOnly(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(4);
            for (int i = 0; i < s.Length && sb.Length < 4; i++) {
                char c = char.ToUpperInvariant(s[i]);
                if (c >= 'A' && c <= 'Z') sb.Append(c);
            }
            return sb.ToString();
        }

        // 加入流程：先离开当前房间 -> 轮询等待离开完成 -> 再加入目标房间；反射一律走运行时类型。
        private static System.Collections.IEnumerator JoinFlow(string id, bool useCode, Matchmaker mm, Action<bool> done) {
            // 优先用游戏自己的 Matchmaker.IsInLobby()（Steam/Unity 各自实现）。
            System.Reflection.FieldInfo inLobby = SRApi.Ref.Field(mm.GetType(), "inPlatformLobbyNoRelay",
                "联机：判断客户端是否已在平台房间里（加入前必须先退出）");
            // 不能静默：反射退化会跳过“先退旧房”，而 JoinLobby 在平台房间里必定失败。
            bool InLobby() {
                try { return mm.IsInLobby(); }   // 游戏自报的权威状态（含 Unity relay / Steam 两条链）
                catch (Exception __il) {
                    SRApi.Guard.Log("联机/读取平台房间状态", __il);
                    try { return inLobby != null && (bool)inLobby.GetValue(mm); }   // 兜底：老版本反射
                    catch { return false; }
                }
            }
            if (InLobby()) {
                try { SRApi.LogInfo("[联机] 已在房间中，先离开..."); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                try {
                    try { mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.lo); }
                    catch (Exception __lv2) { SRApi.Guard.Log("联机.离开房间", __lv2); }
                } catch (Exception __lv) { SRApi.Guard.Log("联机.离开房间", __lv); }
                // 最多等 2.5 秒：仍在平台房间里时 JoinLobby 必定失败，超时补发一次 LeaveLobby 再继续。
                float t = 0f;
                while (InLobby() && t < 2.5f) { t += Time.unscaledDeltaTime; yield return null; }
                if (InLobby()) {
                    try {
                        mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.hi);
                        SRApi.LogWarn("[联机] 等待离开超时，已补发一次 LeaveLobby");
                    } catch (Exception __lv3) { SRApi.Guard.Log("联机.补发离开房间", __lv3); }
                }
                try { SRApi.LogInfo("[联机] 离开完成(inLobby=" + InLobby() + ")，开始加入"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (InLobby()) {
                    // 仍是平台房间成员：JoinLobby 必定失败，提前告知并结束本次加入。
                    SRApi.LogWarn("[联机] 旧房仍未退出，改走原生返回主界面后请再点一次加入");
                    try { Matchmaker.ReturnToMainMenu(null); } catch (Exception __rm) { SRApi.Guard.Log("联机/加入失败后返回主界面", __rm); }
                    if (done != null) done(false);
                    yield break;
                }
            }
            // Steam 通道判定放在 JoinFlow：它是两条加入路径（直接/延时）的汇聚点。
            // sLobbyID 在 Steam 平台是 GUID，真正的房间号在 UnityMatchID。
            if (_pendingSteamId != 0) {
                ulong sid = _pendingSteamId;
                _pendingSteamId = 0;   // 无论下面是否走通都必须清零，否则会污染下一次刷新
                try {
                    // 保持反射：源码里写 SteamMatchmaker 会因同族重载的 CSteamID 形参被迫引入 Steamworks 程序集依赖。
                    System.Reflection.MethodInfo jsu = RefCache.MethodOf(mm.GetType(), "JoinSteamLobby", typeof(ulong));
                    if (jsu != null) {
                        SRApi.LogInfo("[联机] 走 JoinSteamLobby(ulong) " + sid);
                        jsu.Invoke(mm, new object[] { sid });
                        if (done != null) done(true);
                        yield break;   // 已按 Steam 通道发出加入请求，不再走通用 JoinLobby
                    }
                    SRApi.LogWarn("[联机] 无 JoinSteamLobby(ulong)，退回通用流程");
                } catch (Exception __jsl) { SRApi.Guard.Log("联机.JoinSteamLobby", __jsl); }
            }
            try {
                mm.JoinLobby(id, useCode, null);
                if (done != null) done(true);
            } catch (Exception __jl) {
                SRApi.Guard.Log("联机.加入", __jl);
                if (done != null) done(false);
            }
        }


        internal static void JoinLobbySteam(ulong steamId, string id) {
            _pendingSteamId = steamId;
            JoinLobbyInternal(id, false, true);
        }

        public static void JoinLobby(string id, bool useCode) { JoinLobbyInternal(id, useCode, false); }

        // 加入抑制窗口：重复点击会并发两条 JoinFlow 协程；用时间窗以便异常时自然过期。
        private static float _joinGuardUntil;
        private static bool _pendingEscalated;


        // 每帧维护加入状态：进房成功清“在途”；离开房间后清“已加入房号”。
        internal static void TickJoinState() {
            try {
                bool inSession = InSession();
                if (inSession) {
                    if (_joinInFlight) { _joinInFlight = false; SRApi.LogInfo("[联机] 已进入房间，清除加入在途状态"); }
                } else if (!string.IsNullOrEmpty(_joinedLobbyId) && !_joinInFlight) {
                    _joinedLobbyId = "";
                }
                if (_joinInFlight && Time.unscaledTime > _joinInFlightUntil) {
                    _joinInFlight = false;
                    _joinedLobbyId = "";
                    SRApi.LogWarn("[联机] 加入在途超过 10 秒未完成，已解除在途状态（可重新点击加入）");
                }
            } catch (Exception __ex) { SRApi.Guard.Log("加入状态维护", __ex); }
        }

        // 已发起/已加入的目标房号（sLobbyID 或 UnityMatchID 字符串）：列表据此标记并禁止再点。
        private static string _joinedLobbyId = "";
        private static bool _joinInFlight;
        private static float _joinInFlightUntil;
        internal static bool JoinInFlight { get { return _joinInFlight && Time.unscaledTime < _joinInFlightUntil; } }
        internal static string JoinedLobbyId { get { return _joinedLobbyId; } }
        private const float JoinGuardSeconds = 1.5f;   // 只挡双击；失败后可立刻重试

        private static void JoinLobbyInternal(string id, bool useCode, bool keepSteamId) {
            if (!keepSteamId) _pendingSteamId = 0;
            if (string.IsNullOrEmpty(id)) return;
            try {
                if (!string.IsNullOrEmpty(_joinedLobbyId) && _joinedLobbyId == id && InSession()) {
                    Notes("已在该房间中", "Already in this session");
                    return;
                }
            } catch (Exception __aj) { SRApi.Guard.Log("加入/已加入判定", __aj); }
            if (JoinInFlight) {
                Notes("正在加入，请稍后", "Already joining; please wait");
                return;
            }
            // 连点/双击：窗口内只认第一次，第二次给一句提示。
            if (Time.unscaledTime < _joinGuardUntil) {
                Notes("正在加入，请稍候", "Already joining; please wait");
                return;
            }
            _joinGuardUntil = Time.unscaledTime + JoinGuardSeconds;
            DisarmWatchdog();
            if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) {
                // 已在房间里再点另一个房间：只发 LeaveLobby 会让 NetManager 处于"已初始化但未连接"的
                // 半断开状态，之后的 JoinLobby 必定失败且原有会话彻底断掉。统一先回主界面再加入。
                _pendingJoin = id;
                _pendingIsJoin = true;   // 看门狗强制回主界面时保留这个目标
                _pendingUseCode = useCode;
                _pendingWaitFrom = Time.unscaledTime;
                _pendingNudged = false;
                _pendingGiveUpAt = Time.unscaledTime + 15f;   // 等待离开旧房：留出"回主界面"兜底的时间
                Notes("正在退出当前房间并在退出后加入", "Leaving the current session, then joining");
                EscalateToMainMenu("已在房间中切换");
                return;
            }
            DoJoin(id, useCode);
        }

        // 直接离开当前联机（收尾交给游戏的 AbortGame）；退出干净后由 TickPendingJoin 发加入请求。
        private static void LeaveCurrentForJoin() {
            _pendingEscalated = false;
            // 房主在对局中时平台 LeaveLobby 结束不了对局，直接走游戏原生返回更及时。
            try {
                LobbyManager lm0 = LobbyManager.instance;
                if (lm0 != null && lm0.IsHost && lm0.IsInOnlineGame) {
                    SRApi.LogInfo("[联机] 房主在对局中切换房间：直接走原生返回主界面");
                    EscalateToMainMenu("房主在对局中");
                    ArmWatchdog();
                    return;
                }
            } catch (Exception __lh) { SRApi.Guard.Log("联机/房主切换判定", __lh); }
            bool left = false;
            try {
                Matchmaker mm = MatchmakerOrNull();
                if (mm != null) {
                    // reason 传 null 不弹提示；第二个参数必须保留（LeaveLobby 只有一个重载）。
                    try { SRApi.LogInfo("[联机] 离开当前房间以切换（reason 传 null，不弹提示）"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.hi);
                    left = true;
                }
            } catch (Exception e) { SRApi.LogWarn("切换房间前退出失败: " + e.Message); }
            if (!left) {
                // 拿不到匹配器（对局中匹配器常已被销毁）：走游戏自己的返回主界面流程。
                EscalateToMainMenu("无匹配器");
            }
            ArmWatchdog();
        }

        // 升级路径：走游戏自己的 ReturnToMainMenu；不能调用 GoMainMenu()（它清掉待加入意图）。
        private static void EscalateToMainMenu(string why) {
            if (_pendingEscalated) return;
            _pendingEscalated = true;
            try {
                SRApi.LogWarn("[联机] 旧房未退出（" + why + "），改走游戏原生返回主界面，返回后再加入");
                NudgeSplash();
                Matchmaker.ReturnToMainMenu(null);
                // 用游戏自己的“主界面加载完成”回调发加入请求，比轮询场景名更准。
                string __target = _pendingJoin;
                bool __useCode = _pendingUseCode;
                LobbyManagerManager.WaitForMainMenu(delegate {
                    try {
                        if (string.IsNullOrEmpty(_pendingJoin) || _pendingJoin != __target) return;   // 目标已变/已取消
                        ClearPendingJoin("已回主界面");
                        DoJoin(__target, __useCode);
                        DisarmWatchdog();
                    } catch (Exception __wm) { SRApi.Guard.Log("联机/主界面回调加入", __wm); }
                });
            } catch (Exception __ex) { SRApi.Guard.Log("联机/升级为返回主界面", __ex); }
            ArmWatchdog();
        }

        private static bool InSession() {
            try {
                LobbyManager lm = LobbyManager.instance;
                // 游戏的 IsInOnlineGame 在非房主分支恒为 true，故补一层“客户端是否真连着”。
                bool connected = false;
                try { connected = lm != null && lm.client != null && lm.client.isConnected; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (lm != null && (lm.IsHost || (connected && lm.IsInOnlineGame))) return true;
                if (Matchmaker.CurrentMatchmakingLobby != null) return true;
            } catch (Exception __ex) { SRApi.Guard.Log("判定是否在联机中", __ex); }
            return false;
        }

        // 换场景（尤其多次返回主界面）后复位所有“加入瞬时状态”，否则下次点加入会被忽略。
        internal static void ResetJoinStateOnScene() {
            try {
                _joinInFlight = false;
                _joinInFlightUntil = 0f;
                _joinGuardUntil = 0f;
                _pendingEscalated = false;
                _pendingSteamId = 0;
                _joinedLobbyId = "";
                SRApi.LogInfo("[联机] 换场景：已复位加入瞬时状态");
            } catch (Exception __ex) { SRApi.Guard.Log("联机/复位加入状态", __ex); }
        }

        private static void DoJoin(string id, bool useCode) {
            try {
                Matchmaker mm = MatchmakerOrNull();
                if (mm == null) {
                    Notes("联机功能未初始化，请先进入一次游戏的联机界面", "Online is not initialised; open the in-game online screen once");
                    ClearPendingJoin("匹配器不可用");
                    _pendingSteamId = 0;   // 这条加入意图作废，房号不能留到下一次
                    _joinGuardUntil = 0f;
                    return;
                }
                Notes(useCode ? "正在按房间码加入" : "正在加入房间", useCode ? "Joining by code" : "Joining the session");
                // 失败不重试。
                // 加入前必须把 StartAsHost 置 false，否则 Steam 侧会当成创建房间。
                try {
                    GameSettings.GetInstance().StartAsHost = false;
                    SRApi.LogInfo("[联机] StartAsHost=false（加入而非创建）");
                } catch (Exception __sa) { SRApi.Guard.Log("联机.StartAsHost", __sa); }
                try { ArmAutoEnterMainMenu(); } catch (Exception __ae) { SRApi.Guard.Log("联机/启用自动进入", __ae); }
                try { SRApi.LogInfo("[联机] 加入请求 id=" + id + " mm=" + mm.GetType().FullName + " useCode=" + useCode); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                // 必须在此清掉待加入队列：它每帧轮询，不清会重复发起同一次加入。
                ClearPendingJoin("加入请求已发出");
                _pendingJoinId = id;
                _joinInFlight = true;
                _joinInFlightUntil = Time.unscaledTime + 10f;   // 加载超过 10 秒还没进去就允许重试
                // 协程宿主必须是常驻对象：挂在 Matchmaker 上会随切场景/离房被销毁。
            try { CoroutineHost().StartCoroutine(JoinFlow(id, useCode, mm, JoinFlowDone)); return; }
            catch (Exception __jf) { SRApi.Guard.Log("联机.加入流程", __jf); }
                try { SRApi.LogInfo("[联机] 协程启动失败，退回直接 JoinLobby"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                mm.JoinLobby(id, useCode, new UnityEngine.Events.UnityAction<bool>(ok => {
                    try {
                        ClearPendingJoin("加入回调完成");
                        if (ok) _joinedLobbyId = id;
                        else { _joinedLobbyId = ""; _joinInFlight = false; _joinGuardUntil = 0f; }
                        Notes(ok ? "已发送加入请求" : "加入失败", ok ? "Join request sent" : "Join failed");
                    } catch (Exception __ex) { SRApi.Guard.Log("加入房间回调", __ex); }
                }));
                ClearPendingJoin("回退路径已发出");
            } catch (Exception e) {
                SRApi.LogWarn("加入房间失败: " + e.Message);
                ClearPendingJoin("加入异常");
                _joinGuardUntil = 0f;
                _joinInFlight = false;
                _joinedLobbyId = "";
            }
        }

        // JoinFlow 结果回传：成功才把房号记成“已加入”，否则该房会永久标成已进入、点不动。
        private static void JoinFlowDone(bool ok) {
            try {
                if (ok) {
                    _joinedLobbyId = _pendingJoinId;
                    return;
                }
                _joinedLobbyId = "";
                _joinInFlight = false;
                _joinGuardUntil = 0f;
                Notes("加入失败：没能退出上一个房间", "Join failed: could not leave the previous lobby");
                SRApi.LogWarn("[联机] 加入流程失败，已清空已加入房号与在途状态");
            } catch (Exception __ex) { SRApi.Guard.Log("联机/加入结果回传", __ex); }
        }

        // DoJoin 记下本次目标房号供 JoinFlowDone 使用（回调时 pending 已清空）。
        private static string _pendingJoinId = "";

        // 对局内加入：等退出当前房间后再发一次加入请求，只发一次。
        private static void TickPendingJoin() {
            if (string.IsNullOrEmpty(_pendingJoin)) return;
            try {
                float now = Time.unscaledTime;
                if (now > _pendingGiveUpAt) {
                    ClearPendingJoin("等待超时");
                    _pendingSteamId = 0;   // 目标作废，房号不能留到下一次
                    _joinGuardUntil = 0f;   // 超时取消：立刻允许重新点
                    Notes("加入超时，已取消", "Join timed out and was cancelled");
                    return;
                }
                float waited = now - _pendingWaitFrom;
                // 平台房间已退出且不在对局中 = 旧房确实退干净了，可立刻加入。
                bool leftPlatform = false;
                try {
                    Matchmaker __mm = MatchmakerOrNull();
                    bool inMatch = false;
                    try { LobbyManager __lm = LobbyManager.instance; inMatch = __lm != null && __lm.CurrentGameController != null; }
                    catch (Exception __li) { SRApi.Guard.Log("联机/判断是否在对局", __li); }
                    leftPlatform = __mm != null && !__mm.IsInLobby() && !inMatch;
                } catch (Exception __lp) { SRApi.Guard.Log("联机/判断平台房间状态", __lp); }
                bool stillIn = !leftPlatform && (InSession() || Matchmaker.CurrentMatchmakingLobby != null);
                // 已在主界面 = 旧房一定退干净了（InSession() 可能残留旧状态对象）。
                bool atMainMenu = false;
                try { atMainMenu = SceneManager.GetActiveScene().name == "MainMenu"; } catch (Exception __mn) { SRApi.Guard.Log("联机/读取场景名", __mn); }
                if (stillIn && !atMainMenu) {
                    // 2.5 秒还没退出：先放遮罩，再升级为“走游戏原生返回主界面”。
                    if (waited > 1.2f && !_pendingNudged) {
                        _pendingNudged = true;
                        SRApi.LogWarn("[联机] 离开房间已等 " + waited.ToString("0.0") + " 秒，放出加载遮罩并走原生返回");
                        NudgeSplash();
                        EscalateToMainMenu("超过 1.2 秒未退出");
                    }
                    // 15 秒仍未到主界面也没退出：放弃（此时无法安全加入）。
                    return;
                }
                if (atMainMenu && stillIn) SRApi.LogInfo("[联机] 已回主界面，忽略残留的联机状态，继续加入");
                if (_pendingEscalated && !atMainMenu && stillIn) return;
                string id = _pendingJoin;
                bool useCode = _pendingUseCode;
                DoJoin(id, useCode);
                // 已退出旧房并发出加入请求：解除看门狗（未退干净时走不到这里，仍受其兜底）。
                DisarmWatchdog();
            } catch (Exception __ex) { SRApi.Guard.Log("延时加入房间", __ex); }
        }
        // 常驻协程宿主：DontDestroyOnLoad，切场景不会销毁。
        private sealed class CoHost : UnityEngine.MonoBehaviour { }
        private static CoHost _coHost;
        private static CoHost CoroutineHost() {
            if (_coHost != null) return _coHost;
            try {
                GameObject go = new GameObject("SR_UCH.OnlineCo");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _coHost = go.AddComponent<CoHost>();
            } catch (Exception __ch) { SRApi.Guard.Log("联机/创建协程宿主", __ch); }
            return _coHost;
        }


    }
}
