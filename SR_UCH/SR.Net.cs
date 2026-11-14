// 联机版本/功能握手：双方 SR 的功能表签名不一致时提示（纯提示，不改任何游戏状态）。
//
// 做法：复用游戏已有的 GameRuleSet 消息做"带外信号"（与混乱牌组 771/772 魔数错开，用 773），
// 消息体 NewRule=None 表示不是真实规则变更；游戏/其它功能都按各自魔数忽略。
//   - 每个客户端每 20 秒向服务器发一次本机签名；
//   - 房主每 20 秒用 SendToAll 广播一次自己的签名（Valueb=true 标"来自房主"）；
//   - 收到对方签名且与本地不同 -> 记一次日志 + 弹一条提示（同一签名只提示一次）。
// 为什么需要：UCH 锁房，双方装的 SR 功能/版本不同时，界面与行为可能不一致（例如只有一方有某个开关），
// 这个提示能把"为什么我们不一样"直接说清楚。
using System;
using GameEvent;   // GameEventManager / IGameEventListener 在 namespace GameEvent 内
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
public partial class SR {

    private const int CompatMagic = 773;      // 与混乱牌组的 771 / 772 区分
    private const float CompatInterval = 20f; // 广播间隔（秒）

    private static bool _compatInited;
    private static float _compatNextAt;
    private static CompatListener _compatListener;
    private static int _compatHash;           // 本地功能签名（首次发送时计算并缓存）
    private static int _compatCount;          // 本地功能数量
    private static int _compatRemoteHash;     // 最近一次收到的对方签名（去重提示）

    private sealed class CompatListener : GameEvent.IGameEventListener {
        public void handleEvent(GameEvent.GameEvent e) {
            try { SR.OnCompatMessage(e); } catch (Exception __ex) { Guard.Log("联机握手/收包", __ex); }
        }
    }

    internal static void NetInit() {
        if (_compatInited) return;
        _compatInited = true;
        try {
            _compatListener = new CompatListener();
            GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_compatListener, true);
            SRApi.NetMagic.Register("core.compat", CompatMagic);
            LatencyInit();   // 延迟探测回显（EX 的「延迟」列用；对方只要装 SR 就能被测量）
            RegisterTick("联机兼容握手", NetTick);
            LogInfo("[SR.Net] 联机版本握手已启用（联机时每 " + (int)CompatInterval + " 秒比对一次功能签名）");
        } catch (Exception __ex) { Guard.Log("联机握手/初始化", __ex); }
    }

    internal static void NetShutdown() {
        try {
            if (_compatListener != null)
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_compatListener, false);
        } catch (Exception __ex) { Guard.Log("联机握手/注销", __ex); }
        _compatListener = null;
        _compatInited = false;
        LatencyShutdown();
    }

    // 本地签名：所有功能 id 排序后做 FNV-1a，再混入契约版本。
    // 功能增删/改名/契约版本变化都会改变签名；同一版本号但功能表不同也能识别出来。
    private static int LocalCompatHash() {
        if (_compatHash != 0) return _compatHash;
        try {
            unchecked {
                int h = (int)2166136261;
                System.Collections.Generic.List<string> ids = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Features.All.Count; i++) ids.Add(Features.All[i].Id ?? "");
                ids.Sort(StringComparer.Ordinal);
                for (int i = 0; i < ids.Count; i++) {
                    string s = ids[i];
                    for (int c = 0; c < s.Length; c++) { h ^= s[c]; h *= 16777619; }
                    h ^= 0x1F; h *= 16777619;
                }
                h ^= SRContract.Version * 73856093;
                _compatCount = ids.Count;
                _compatHash = h == 0 ? 1 : h;
            }
        } catch (Exception __ex) { Guard.Log("联机握手/本地签名", __ex); }
        return _compatHash;
    }

    private static void SendCompat() {
        try {
            if (!NetworkClient.active && !NetworkServer.active) return;
            LocalCompatHash();
            MsgGameRuleSet msg = new MsgGameRuleSet();
            msg.NewRule = TabletRule.None;                              // 不是真实规则变更
            msg.Value = _compatHash;
            msg.Value2 = (CompatMagic << 8) | (_compatCount & 0xFF);
            bool isHost = NetworkServer.active;
            msg.Valueb = isHost;                                        // true = 来自房主
            if (isHost) {
                NetworkServer.SendToAll(NetMsgTypes.GameRuleSet, msg);
                return;
            }
            if (LobbyManager.instance != null && LobbyManager.instance.client != null)
                LobbyManager.instance.client.Send(NetMsgTypes.GameRuleSet, msg);
        } catch (Exception __ex) { Guard.Log("联机握手/发包", __ex); }
    }

    private static void NetTick() {
        try {
            if (Time.unscaledTime < _compatNextAt) return;
            _compatNextAt = Time.unscaledTime + CompatInterval;
            SendCompat();
        } catch (Exception __ex) { Guard.Log("联机握手/轮询", __ex); }
    }

    private static void OnCompatMessage(GameEvent.GameEvent e) {
        GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
        if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.GameRuleSet) return;
        MsgGameRuleSet m = nm.ReadMessage as MsgGameRuleSet;
        if (m == null || m.NewRule != TabletRule.None) return;          // 只认带外信号
        if ((m.Value2 >> 8) != CompatMagic) return;                     // 其它功能/游戏的规则消息
        int remoteHash = m.Value;
        int remoteCount = m.Value2 & 0xFF;
        if (remoteHash == 0 || remoteHash == _compatRemoteHash) return; // 同一签名只处理一次
        _compatRemoteHash = remoteHash;
        int mine = LocalCompatHash();
        if (remoteHash == mine) {
            LogInfo("[SR.Net] 联机握手一致：双方 SR 功能签名相同（" + _compatCount + " 个功能）");
            return;
        }
        LogInfo("[SR.Net] 联机握手不一致：对方功能 " + remoteCount + " 个 / 签名 " + remoteHash
                + "，本地 " + _compatCount + " 个 / 签名 " + mine);
        Notify(T("联机双方 SR 版本/功能不一致（对方 " + remoteCount + " 个 / 本地 " + _compatCount + " 个），界面与行为可能不同；建议使用同一版本。",
                 "SR features differ between peers (" + remoteCount + " remote / " + _compatCount + " local); UI and behaviour may differ - use the same version."));
    }
}
}
