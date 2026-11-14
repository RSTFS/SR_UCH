// 延迟探测回显：回显逻辑放核心，对方只装基础模组也能被房主测到 RTT。
// 协议同 ExModule.Ping.cs：MsgGameRuleSet + Magic=774，NewRule=None 的带外信号。
using System;
using GameEvent;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    public partial class SR {
        private const int LatencyMagic = 774;   // 与核心握手 773、混乱牌组 771/772 区分

        private sealed class LatencyListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try { OnLatencyMessage(e); } catch (Exception __ex) { Guard.Log("延迟回显/收包", __ex); }
            }
        }
        private static LatencyListener _latencyListener;

        internal static void LatencyInit() {
            try {
                SRApi.NetMagic.Register("core.ping", LatencyMagic);   // 与 EX 的登记同名同值，重复登记不冲突
                _latencyListener = new LatencyListener();
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_latencyListener, true);
            } catch (Exception __ex) { Guard.Log("延迟回显/初始化", __ex); }
        }

        internal static void LatencyShutdown() {
            try {
                if (_latencyListener != null)
                    GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_latencyListener, false);
            } catch (Exception __ex) { Guard.Log("延迟回显/注销", __ex); }
            _latencyListener = null;
        }

        private static void OnLatencyMessage(GameEvent.GameEvent e) {
            GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
            if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.GameRuleSet) return;
            MsgGameRuleSet m = nm.ReadMessage as MsgGameRuleSet;
            if (m == null || m.NewRule != TabletRule.None) return;
            if ((m.Value2 >> 16) != LatencyMagic) return;
            if (NetworkServer.active) return;   // 房主端只负责测量，不回显（避免自己 ping 自己）
            try {
                if (LobbyManager.instance != null && LobbyManager.instance.client != null) {
                    MsgGameRuleSet echo = new MsgGameRuleSet();
                    echo.NewRule = TabletRule.None;
                    echo.Value = 0;
                    echo.Value2 = m.Value2;
                    echo.Valueb = false;
                    LobbyManager.instance.client.Send(NetMsgTypes.GameRuleSet, echo);
                }
            } catch (Exception __ex) { Guard.Log("延迟回显/发送", __ex); }
        }
    }
}
