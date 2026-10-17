// Online 的 partial 分片（只做代码组织，无逻辑改动）
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
        // 点加入：不在房间里直接加；在房间里则先退出当前房间，退出后立刻加入，不等回主界面。
        public static void JoinLobby(string id) { JoinLobby(id, false); }

        // 按房间码加入，走游戏自己的 Matchmaker.JoinLobby(code, useCode: true)。
        public static void JoinByCode(string code) {
            if (string.IsNullOrEmpty(code)) return;
            JoinLobby(code.Trim(), true);
        }

        // 房间码输入过滤：只保留大写英文字母，最多 4 个。
        private static string CodeOnly(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(4);
            for (int i = 0; i < s.Length && sb.Length < 4; i++) {
                char c = char.ToUpperInvariant(s[i]);
                if (c >= 'A' && c <= 'Z') sb.Append(c);
            }
            return sb.ToString();
        }

        // 加入流程：先离开当前房间 -> 轮询等待离开完成 -> 再加入目标房间。
        // inPlatformLobbyNoRelay 是私有字段且声明在子类上，必须用 mm.GetType()（运行时类型）反射。
        private static System.Collections.IEnumerator JoinFlow(string id, bool useCode, Matchmaker mm) {
            // 反射游戏私有成员必须走 SRApi.Ref.Field（带功能名提示、失败告警与结果缓存）。
            // 类型必须是运行时类型：Steam 与 Unity 的匹配器是两条独立继承链，字段声明在各自的链上。
            System.Reflection.FieldInfo inLobby = SRApi.Ref.Field(mm.GetType(), "inPlatformLobbyNoRelay",
                "联机：判断客户端是否已在平台房间里（加入前必须先退出）");
            bool InLobby() { try { return inLobby != null && (bool)inLobby.GetValue(mm); } catch { return false; } }
            if (InLobby()) {
                try { SRApi.LogInfo("[联机] 已在房间中，先离开..."); } catch { }
                try {
                    // LeaveLobby 声明在基类 Matchmaker 上，直接调用交由虚方法分发。
                    try { mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.lo); }
                    catch (Exception __lv2) { SRApi.Guard.Log("联机.离开房间", __lv2); }
                } catch (Exception __lv) { SRApi.Guard.Log("联机.离开房间", __lv); }
                // 等待离开最多 2.5 秒：仍在平台房间里时通用 JoinLobby 必定失败；超时后补发一次 LeaveLobby 再继续。
                float t = 0f;
                while (InLobby() && t < 2.5f) { t += Time.unscaledDeltaTime; yield return null; }
                if (InLobby()) {
                    try {
                        mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.hi);
                        SRApi.LogWarn("[联机] 等待离开超时，已补发一次 LeaveLobby");
                    } catch (Exception __lv3) { SRApi.Guard.Log("联机.补发离开房间", __lv3); }
                }
                try { SRApi.LogInfo("[联机] 离开完成(inLobby=" + InLobby() + ")，开始加入"); } catch { }
            }
            // Steam 通道判定放在 JoinFlow：它是两条加入路径（直接加入 / 延时加入）的汇聚点。
            // sLobbyID 在 Steam 平台是 GUID，真正的 Steam 房间号在 UnityMatchID。
            if (_pendingSteamId != 0) {
                ulong sid = _pendingSteamId;
                _pendingSteamId = 0;   // 无论下面是否走通都必须清零，否则会污染下一次刷新
                try {
                    // 保持反射：源码里写 SteamMatchmaker 会因同族重载的 CSteamID 形参被迫引入 Steamworks 程序集依赖。
                    System.Reflection.MethodInfo jsu = mm.GetType().GetMethod("JoinSteamLobby", new System.Type[] { typeof(ulong) });
                    if (jsu != null) {
                        SRApi.LogInfo("[联机] 走 JoinSteamLobby(ulong) " + sid);
                        jsu.Invoke(mm, new object[] { sid });
                        yield break;   // 已按 Steam 通道发出加入请求，不再走通用 JoinLobby
                    }
                    SRApi.LogWarn("[联机] 无 JoinSteamLobby(ulong)，退回通用流程");
                } catch (Exception __jsl) { SRApi.Guard.Log("联机.JoinSteamLobby", __jsl); }
            }
            try { mm.JoinLobby(id, useCode, null); } catch (Exception __jl) { SRApi.Guard.Log("联机.加入", __jl); }
        }


        // 按 Steam 房间号加入，由表格路径显式调用。
        // Steam 平台拿到的 mm 是 SteamMatchmaker，与 UnityMatchmaker 是两条独立继承链：反射必须用 mm.GetType()。
        internal static void JoinLobbySteam(ulong steamId, string id) {
            _pendingSteamId = steamId;
            JoinLobbyInternal(id, false, true);
        }

        public static void JoinLobby(string id, bool useCode) { JoinLobbyInternal(id, useCode, false); }

        private static void JoinLobbyInternal(string id, bool useCode, bool keepSteamId) {
            if (!keepSteamId) _pendingSteamId = 0;
            if (string.IsNullOrEmpty(id)) return;
            // 玩家点加入即放弃"回主界面"：看门狗必须在这里解除，调用方的两条路径都要覆盖。
            DisarmWatchdog();
            if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) {
                _pendingJoin = id;
                _pendingUseCode = useCode;
                // 记下等待起点，供超时加速与输入屏蔽上限使用。
                _pendingWaitFrom = Time.unscaledTime;
                _pendingNudged = false;
                _pendingGiveUpAt = Time.unscaledTime + 30f;
                Notes("正在退出当前房间并在退出后加入", "Leaving the current session, then joining");
                // 只做"离开当前房间"，不等主界面：不调用 ReturnToMainMenu，省一趟场景加载。
                LeaveCurrentForJoin();
                return;
            }
            DoJoin(id, useCode);
        }

        // 直接离开当前联机（收尾交给游戏的 AbortGame）；退出干净后由 TickPendingJoin 发一次加入请求。
        private static void LeaveCurrentForJoin() {
            try {
                Matchmaker mm = MatchmakerOrNull();
                if (mm != null) {
                    // reason 不能传自定义串：LeaveLobby 会把它弹成 3 秒高优先级提示，传 null 则跳过。
                    // 第二个参数必须保留：LeaveLobby 只有 (string, UserMsgPriority) 一个重载。
                    try { SRApi.LogInfo("[联机] 离开当前房间以切换（reason 传 null，不弹提示）"); } catch { }
                    mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.hi);
                }
            } catch (Exception e) { SRApi.LogWarn("切换房间前退出失败: " + e.Message); }
            ArmWatchdog();
        }

        // 是否正在联机 / 对局中（决定要不要先退出）。
        private static bool InSession() {
            try {
                LobbyManager lm = LobbyManager.instance;
                // 游戏的 IsInOnlineGame 在非房主分支恒为 true，故补一层"客户端是否真的连着"；下面的 CurrentMatchmakingLobby 兜底。
                bool connected = false;
                try { connected = lm != null && lm.client != null && lm.client.isConnected; } catch { }
                if (lm != null && (lm.IsHost || (connected && lm.IsInOnlineGame))) return true;
                if (Matchmaker.CurrentMatchmakingLobby != null) return true;
            } catch (Exception __ex) { SRApi.Guard.Log("判定是否在联机中", __ex); }
            return false;
        }

        private static void DoJoin(string id, bool useCode) {
            try {
                Matchmaker mm = MatchmakerOrNull();
                if (mm == null) {
                    Notes("联机功能未初始化，请先进入一次游戏的联机界面", "Online is not initialised; open the in-game online screen once");
                    _pendingJoin = "";
                    _pendingSteamId = 0;   // 这条加入意图作废，房号不能留到下一次
                    return;
                }
                Notes(useCode ? "正在按房间码加入" : "正在加入房间", useCode ? "Joining by code" : "Joining the session");
                // 失败不重试。
                // 加入前必须把 StartAsHost 置 false，否则 Steam 侧会把请求当成创建房间。
                try {
                    GameSettings.GetInstance().StartAsHost = false;
                    SRApi.LogInfo("[联机] StartAsHost=false（加入而非创建）");
                } catch (Exception __sa) { SRApi.Guard.Log("联机.StartAsHost", __sa); }
                try { SRApi.LogInfo("[联机] 加入请求 id=" + id + " mm=" + mm.GetType().FullName + " useCode=" + useCode); } catch { }
                // 必须在这里清掉待加入队列：它是每帧轮询的，不清会重复发起同一次加入。
                _pendingJoin = "";
                try { mm.StartCoroutine(JoinFlow(id, useCode, mm)); return; } catch (Exception __jf) { SRApi.Guard.Log("联机.加入流程", __jf); }
                try { SRApi.LogInfo("[联机] 协程启动失败，退回直接 JoinLobby"); } catch { }
                mm.JoinLobby(id, useCode, new UnityEngine.Events.UnityAction<bool>(ok => {
                    try {
                        _pendingJoin = "";
                        Notes(ok ? "已发送加入请求" : "加入失败", ok ? "Join request sent" : "Join failed");
                    } catch (Exception __ex) { SRApi.Guard.Log("加入房间回调", __ex); }
                }));
                // 发出即算完成：这里立刻清空，避免回调未触发时 TickPendingJoin 反复重发。
                _pendingJoin = "";
            } catch (Exception e) {
                SRApi.LogWarn("加入房间失败: " + e.Message);
                _pendingJoin = "";
            }
        }

        // 对局内加入：等退出当前房间后再发一次加入请求，只发一次。
        private static void TickPendingJoin() {
            if (string.IsNullOrEmpty(_pendingJoin)) return;
            try {
                float now = Time.unscaledTime;
                if (now > _pendingGiveUpAt) {
                    _pendingJoin = "";
                    Notes("加入超时，已取消", "Join timed out and was cancelled");
                    return;
                }
                // 尚未退出干净：继续等待，不算重试。
                if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) {
                    // 每帧判定离开是否完成（判据都是廉价字段读取），离开一完成立刻发请求。
                    float waited = now - _pendingWaitFrom;
                    if (waited > 2.5f && !_pendingNudged) {
                        _pendingNudged = true;
                        SRApi.LogWarn("[联机] 离开房间已等 " + waited.ToString("0.0") + " 秒，放出加载遮罩加速");
                        NudgeSplash();
                    }
                    return;
                }
                string id = _pendingJoin;
                bool useCode = _pendingUseCode;
                DoJoin(id, useCode);
                // 已退出旧房间并发出加入请求：解除看门狗（未退出干净时不会走到这里，仍受其兜底）。
                DisarmWatchdog();
            } catch (Exception __ex) { SRApi.Guard.Log("延时加入房间", __ex); }
        }
    }
}
