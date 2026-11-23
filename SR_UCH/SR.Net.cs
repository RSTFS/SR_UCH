// 联机版本/功能握手：双方 SR 的功能表签名不一致时提示（纯提示，不改游戏状态）。
// 做法：复用 GameRuleSet 消息做带外信号，魔数 773（与混乱牌组的 771/772 错开），
// 消息体 NewRule=None 表示不是真实规则变更；每 20 秒互发一次本机签名，
// 房主用 SendToAll 广播（Valueb=true 标「来自房主」）；接收端按 Valueb 过滤自身回环与房客互发，
// 签名不同才记警告 + 弹一次提示。
// 为什么需要：UCH 锁房，双方功能/版本不同时界面与行为可能不一致。
using System;
using System.Collections.Generic;
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
    private static readonly HashSet<int> _compatSeen = new HashSet<int>();  // 已提示过的对方签名

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
            LatencyInit();   // 延迟探测回显（EX 的「延迟」列用；对方装 SR 即可测量）
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
        _compatSeen.Clear();
        LatencyShutdown();
    }

    // 本地签名：所有功能 id 排序后做 FNV-1a，再混入契约版本。
    // 功能增删/改名/契约版本变化都会改变签名；同版本号但功能表不同也能识别。
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
        // 房主只处理房客上报，房客只处理房主广播：滤掉 SendToAll 的自身回环与房客互发。
        bool fromHost = m.Valueb;
        bool amHost = NetworkServer.active;
        if (fromHost == amHost) return;
        int remoteHash = m.Value;
        int remoteCount = m.Value2 & 0xFF;
        if (remoteHash == 0) return;
        int mine = LocalCompatHash();
        if (remoteHash == mine) return;                     // 一致：静默，不刷日志
        if (!_compatSeen.Add(remoteHash)) return;            // 同一签名只提示一次
        LogWarn("[SR.Net] 联机握手不一致：对方功能 " + remoteCount + " 个 / 签名 " + remoteHash
                + "，本地 " + _compatCount + " 个 / 签名 " + mine);
        Notify(T("联机双方 SR 版本/功能不一致（对方 " + remoteCount + " 个 / 本地 " + _compatCount + " 个），界面与行为可能不同；建议使用同一版本。",
                 "SR features differ between peers (" + remoteCount + " remote / " + _compatCount + " local); UI and behaviour may differ - use the same version."));
    }
}
}
