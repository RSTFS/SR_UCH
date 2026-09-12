using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using SR_UCH.Tweaks; //SR / Loc

namespace SR_UCH_EX {
public partial class ExModule {

// ==== 分区：Round（获胜 / 复活 / 杀死 / 加生命 / 指定关卡 / 结束回合 / 坐标）====


        //--- 获胜：让自己到达终点获胜。CallCmdSetSuccess 是 Command，只对自己（有权限的
        //角色）生效，经服务器执行 → 全员可见获胜流程。
        public static void WinTarget() {
            try {
                int target = CurrentTarget();
                if (target < 0) return;
                Character tc = GetCharacter(target);
                if (tc == null) { Notify("目标不在场景"); return; }
                if (!tc.hasAuthority) { Notify("仅对自己生效（获胜需自身权限）"); return; }
                tc.CallCmdSetSuccess(true);
                Notify("获胜: " + PlayerName(target));
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("获胜失败: " + e.Message);
            }
        }

        //--- 复活：让目标重生回起点。
        //**树屋为什么不能走原生复活**（反编译确认）：
        //  Character.Respawn() = SetupClientRespawn(); LoseLife(); Disable(); 
        //      LobbyManager.instance.CurrentGameController.LevelLayout.SpawnCharacter(this,0f); ... Enable(); ...
        //  而树屋场景 TreeHouseLobby **没有 LevelLayout**（GameControl 里明确"只有树屋允许 LevelLayout==null"），
        //  所以 SpawnCharacter 那行抛空引用 —— 此时 Disable() 已经执行过（角色被移到 (-1000,-1000)、
        //  隐藏、碰撞关闭、SetLobbyCollider(false)），后面的 Enable() 永远不执行 → **角色假死**。
        //因此：有 LevelLayout（对局内）走原生 CallRpcRespawn；没有（树屋等）走安全路径：
        //  放回记录的大厅位置 + Enable()（Enable 内部会把 disabled/frozen/LocallyDead/dying/dead 全清掉）。
        public static void RespawnTarget() {
            try {
                int target = CurrentTarget();
                if (target < 0) return;
                Character tc = GetCharacter(target);
                if (tc == null) { Notify("目标不在场景"); return; }
                bool hasLayout = false;
                try {
                    GameControl gc = (LobbyManager.instance != null) ? LobbyManager.instance.CurrentGameController : null;
                    hasLayout = gc != null && gc.LevelLayout != null;
                } catch { hasLayout = false; }
                if (!hasLayout) { RespawnSafe(tc, target); return; }
                //服务器端（房主/单机/本地派对）：CallRpcRespawn 是 Rpc，本就只能由服务器调用，
                //所以 NetworkServer.active 已是充分条件 → 房主真正可复活任意目标。
                if (UnityEngine.Networking.NetworkServer.active) {
                    tc.CallRpcRespawn();
                    Notify("复活: " + PlayerName(target));
                    return;
                }
                //房客端：CallCmdRespawn 只能对自己（Command 需自身权限）；复活别人需房主权限
                if (!tc.hasAuthority) {
                    Notify("复活别人需房主/单机权限（房客只能复活自己）");
                    return;
                }
                tc.CallCmdRespawn();
                Notify("复活: " + PlayerName(target));
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("复活失败: " + e.Message);
            }
        }

        //无 LevelLayout 场景（树屋大厅）的安全复活：清死亡状态 + 回到记录的大厅位置
        private static void RespawnSafe(Character tc, int target) {
            try {
                Vector3 home;
                if (_lobbyHome.TryGetValue(tc.networkNumber, out home)) tc.transform.position = home;
                tc.Enable(); //内部：disabled/frozen/LocallyDead/dying/dead/success 等全部复位（有权限时同步）
                Notify("复活: " + PlayerName(target) + "（大厅安全复活）");
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("大厅复活失败: " + e.Message);
            }
        }

        //记录大厅里各角色"进大厅时的位置"（树屋没有 LevelLayout.StartPoint，只能用这个当出生点）。
        //由每帧热键组件调用；换场景时清空。已经被 Disable 移到 -1000 的不记。
        private static readonly Dictionary<int, Vector3> _lobbyHome = new Dictionary<int, Vector3>();
        private static string _lobbyHomeScene = "";
        private static void TrackLobbyHome() {
            try {
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (sc != _lobbyHomeScene) { _lobbyHomeScene = sc; _lobbyHome.Clear(); }
                if (sc != "TreeHouseLobby") return;
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null || p.PlayerCharacter == null) continue;
                    Character c = p.PlayerCharacter;
                    int n = c.networkNumber;
                    if (_lobbyHome.ContainsKey(n)) continue;
                    Vector3 pos = c.transform.position;
                    if (pos.x < -900f || pos.y < -900f) continue; //已被 Disable 移到 (-1000,-1000)
                    _lobbyHome[n] = pos;
                }
            } catch { }
        }

        //--- 杀死目标：直接调用 KillCharacter 立即致死。
        //**权限真相（反编译确认）**：Character.KillCharacter 的 if 条件最后一条是 base.hasAuthority，
        //而角色 authority 由 NetworkServer.SpawnWithClientAuthority(character, gamePlayer) 交给
        //**目标玩家自己的客户端** —— 所以：
        //  · 单机 / 本地派对（同机多角色）：本机拥有全部角色 → 可杀死任意目标；
        //  · 联机房主：远端房客的角色由对方客户端控制，hasAuthority=false → KillCharacter 是空操作
        //    （旧代码不判断这点，会弹出"杀死: XX"的**假成功**提示）；
        //  · 联机房客：对自己可生效（本机拥有该角色），对别人无效。
        //force=true：绕过我们自己的无敌拦截（无敌是"自己开的功能"，不该阻止管理操作）。
        //cause 用 "Trap"（非 Won/Falling/World/Drowning → 游戏按"方块死亡"归类），
        //causedByPlayerNumber 传 0：不给操作者记陷阱击杀分（避免把管理操作算成得分）。
        public static void KillTarget() {
            try {
                int target = CurrentTarget();
                if (target < 0) return;
                Character tc = GetCharacter(target);
                if (tc == null) { Notify("目标不在场景"); return; }
                if (tc.Success) { Notify("目标已到达终点，无法杀死"); return; }
                if (!tc.hasAuthority) {
                    if (UnityEngine.Networking.NetworkServer.active)
                        Notify("杀不死：该角色由对方客户端控制（联机只能杀死本机角色）");
                    else
                        Notify("杀死别人需对方角色权限（房客只能杀死自己）");
                    return;
                }
                tc.KillCharacter("Trap", false, 0, force: true);
                Notify("杀死: " + PlayerName(target));
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("杀死目标失败: " + e.Message);
            }
        }

        //--- 加生命：改自己 GamePlayer.lives（非 SyncVar → 本地显示；单机/本地派对生效）
        public static void AddLives() {
            try {
                int target = CurrentTarget();
                if (target < 0) return;
                int amount = _livesAmount != null ? _livesAmount.Value : 1;
                GamePlayer gp = null;
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null && lm.PlayerTracker != null) gp = lm.PlayerTracker.GetGamePlayer(target);
                } catch { }
                if (gp == null) { Notify("目标不在对局"); return; }
                if (!gp.hasAuthority) { Notify("仅对自己生效（加生命为本地显示）"); return; }
                gp.lives += amount; //public int lives（非 SyncVar）
                Notify("生命" + (amount >= 0 ? "+" : "") + amount + ": " + PlayerName(target));
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("加生命失败: " + e.Message);
            }
        }


        //--- 指定关卡：树屋大厅直接开始所选关卡（LaunchLevel 需服务器权限 →
        //仅单机/本地派对/房主有效；联机房客无效）
        public static void ForceLevel() {
            try {
                GameState.LevelName want = _targetLevel != null ? _targetLevel.Value : GameState.LevelName.FARM;
                LevelSelectController lsc = null;
                if (LobbyManager.instance != null) {
                    try { lsc = LobbyManager.instance.CurrentLevelSelectController; } catch { }
                }
                if (lsc == null) lsc = UnityEngine.Object.FindObjectOfType<LevelSelectController>();
                if (lsc == null || lsc.portals == null) {
                    Notify("不在树屋大厅");
                    return;
                }
                if (!lsc.hasAuthority) {
                    Notify("仅房主/单机有效（指定关卡需服务器权限）");
                    return;
                }
                LevelPortal portal = null;
                foreach (LevelPortal p in lsc.portals) {
                    if (p != null && (int)p.TargetLevel == (int)want) { portal = p; break; }
                }
                if (portal == null) {
                    Notify("大厅没有该关卡门");
                    return;
                }
                portal.StartCountDown();
                lsc.LaunchLevel(portal); //官方启动流程
                Notify("指定关卡: " + SR.EnumNameZh(want.ToString()));
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("指定关卡失败: " + e.Message);
            }
        }


        //--- 结束回合：让当前回合立即进入结算（END 阶段）。走游戏原生 StartPhaseEvent：
        //GameControl.handleEvent 收到 END 后设 nextPhase=END → 主循环 SetupEnd() →
        //房主（服务器）广播 RpcStartPhase(END) 全员结算。公开 API，无反射。
        //需服务器权限：仅单机/本地派对/房主有效；联机房客无效。
        public static void EndRound() {
            try {
                GameControl gc = null;
                if (LobbyManager.instance != null) {
                    try { gc = LobbyManager.instance.CurrentGameController as GameControl; } catch { }
                }
                if (gc == null) gc = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (gc == null) {
                    Notify("未在关卡内");
                    return;
                }
                if (!gc.hasAuthority) {
                    Notify("仅房主/单机有效（结束回合需服务器权限）");
                    return;
                }
                GameEvent.GameEventManager.SendEvent(new GameEvent.StartPhaseEvent(GameControl.GamePhase.END));
                Notify("结束回合");
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("结束回合失败: " + e.Message);
            }
        }


        //--- positions: self + target coordinates for the console
        public static string Positions() {
            try {
                Vector3 me = Vector3.zero;
                Vector3 tgt = Vector3.zero;
                bool haveMe = false, haveTgt = false;
                int cur = CurrentTargetNumber();
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null || p.PlayerCharacter == null) continue;
                    Character c = p.PlayerCharacter;
                    if (c.hasAuthority) { me = c.transform.position; haveMe = true; }
                    if (c.networkNumber == cur) { tgt = c.transform.position; haveTgt = true; }
                }
                return (haveMe ? SR.T("我", "me") + "(" + me.x.ToString("0") + "," + me.y.ToString("0") + ")" : "")
                    + (haveTgt ? " " + SR.T("目标", "tgt") + "(" + tgt.x.ToString("0") + "," + tgt.y.ToString("0") + ")" : "");
            } catch { return ""; }
        }

	}
}
