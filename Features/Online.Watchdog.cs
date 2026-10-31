// 联机：回家看门狗与房主解散感知（Online 的 partial 分片，只做代码组织，无逻辑改动）
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
        // 回家看门狗（只在玩家点了退出联机 / 返回主界面之后启动）。
        // 三级兜底：1.5 秒放遮罩 -> 4 秒自行收尾并强制加载主界面 -> 再 2 秒直接切场景。
        private static void TickWatchdog() {
            float now = Time.unscaledTime;
            // 已回到主界面：清掉时限，避免残留时限让"再点一次返回主界面"误触发强制返回。
            if ((_abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f || _returnAttemptedAt > 0f)
                && SceneManager.GetActiveScene().name == "MainMenu") {
                _abortDeadline = 0f; _forceDeadline = 0f; _forceRawDeadline = 0f;
                // 必须一并清零：否则"上次返回未完成"会永久成立，之后每次「退出/解散」都直接走
                // ForceMainMenu，跳过 GoMainMenu 的 HostEndedGame 广播（评审 B-1）。
                _returnAttemptedAt = 0f;
            }
            if (_abortDeadline > 0f && now >= _abortDeadline) {
                _abortDeadline = 0f;
                string sc = SceneManager.GetActiveScene().name;
                if (sc == "MainMenu") { _forceDeadline = 0f; return; }
                SRApi.LogWarn("[联机] 回家超时（场景 " + sc + "）-> 放出加载遮罩让流程继续");
                NudgeSplash();
            }
            if (_forceDeadline > 0f && now >= _forceDeadline) {
                _forceDeadline = 0f;
                if (SceneManager.GetActiveScene().name == "MainMenu") return;
                SRApi.LogWarn("[联机] 仍卡在加载动画 -> 强制回主界面");
                NudgeSplash();   // 先放出被等待的遮罩再收尾
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        // 同 ForceMainMenu：每步独立 try，失败必须留痕。
                        try { lm.Disconnect("SR_UCH watchdog"); } catch (Exception __ex) { SRApi.Guard.Log("超时兜底：断开连接", __ex); }
                        try { UnityEngine.Object.Destroy(lm.gameObject); } catch (Exception __ex) { SRApi.Guard.Log("超时兜底：销毁 LobbyManager", __ex); }
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("超时兜底：收尾联机状态", __ex); }
                try { SceneManagerWrapper.LoadScene("MainMenu"); }
                catch (Exception e) { SRApi.LogWarn("强制加载主界面失败: " + e.Message); }
                _forceRawDeadline = now + 2f;
            }
            // 二级兜底：温和加载 2 秒后仍未进主界面则直接切场景。
            if (_forceRawDeadline > 0f && now >= _forceRawDeadline) {
                _forceRawDeadline = 0f;
                if (SceneManager.GetActiveScene().name == "MainMenu") return;
                SRApi.LogWarn("[联机] 温和加载仍未进主界面 -> 直接切场景 MainMenu");
                try { SceneManager.LoadScene("MainMenu"); }
                catch (Exception e) { SRApi.LogWarn("直接切主界面失败: " + e.Message); }
            }
        }

        // 房主解散感知：房主返回主界面时广播 HostEndedGame，房客侧据此收尾本模组的待加入队列与看门狗。
        public class HostEndedListener : IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    NetworkMessageReceivedEvent nm = e as NetworkMessageReceivedEvent;
                    if (nm == null || nm.Message == null) return;
                    if (nm.Message.msgType != NetMsgTypes.HostEndedGame) return;
                    OnHostEnded();
                } catch (Exception __ex) { SRApi.Guard.Log("房主解散感知", __ex); }
            }
        }

        private static void OnHostEnded() {
            try {
                // 1) 取消未完成的加入：房主已不在，继续等只会超时。
                bool wasPending = !string.IsNullOrEmpty(_pendingJoin);
                ClearPendingJoin("房主解散");
                // 2) 解除回家看门狗，避免它的超时兜底强断连接 / 销毁 LobbyManager。
                DisarmWatchdog();
                ClearLobbyList();
                try { SRApi.ForceResetUiState(); } catch (Exception __ex) { SRApi.Guard.Log("房主解散：重置界面状态", __ex); }
                NudgeSplash();
                // 只武装"软"看门狗：8 秒内还没回到主界面才切场景，
                // 全程不碰 LobbyManager（硬看门狗的 Disconnect + Destroy 会打断游戏自己的收尾流程）。
                ArmWatchdogSoft();
                if (wasPending) Notes("房主已解散房间，已取消加入", "Host disbanded; join cancelled");
                else Notes("房主已解散房间", "Host disbanded");
                SRApi.LogInfo("[联机] 收到 HostEndedGame：已清待加入队列，改挂软看门狗（8 秒后仍未回主界面才切场景）");
            } catch (Exception __ex) { SRApi.Guard.Log("处理房主解散", __ex); }
        }
    }
}
