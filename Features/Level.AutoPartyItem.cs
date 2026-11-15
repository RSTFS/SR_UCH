// 关卡 · 自动道具（AutoPartyItem）：派对盒开启后自动挑一个道具。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
[SrFeature("sr.autopartyitem", "AutoPartyItem", "1.0")]
public class AutoPartyItem : ITweak {
    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<float> _delay;
    private static ConfigEntry<bool> _skipBomb;

    private static System.Reflection.FieldInfo _fPieces;
    private static float _armedAt = -1f;      // 本次开箱开始计时的时刻
    // 开箱序号取自光标进入挑选态的次数；派对盒实例全大厅复用，用实例 ID 会永远判为已挑。
    private static int _openSeq;
    private static int _pickedSeq = -1;
    private static bool _cursorWasPicking;
    private static float _nextAt = -999f;
    // 悬停挑选：先把本地光标挪到目标道具上让游戏算出 hoveredPick，下一拍再走 tryPickPiece。
    private static PartyPickCursor _hpCur;
    private static PickableBlock _hpTarget;
    private static int _hpStage;          // 0=空闲 1=把光标挪到目标上 2=启动协程 3=等待验收
    private static Vector3 _hpSavedPos;   // 光标原位（挑完还回去，用户看不到位移）
    private static bool _hpMoved;
    private static float _hpAt = -1f;
    private static int _hpAttempts;

    private static void SelfReg() {
        SRApi.LocKey("Level", "Auto Party Item", "自动道具", null);
        SRApi.LocDesc("Level", "Auto Party Item", "自动道具\n派对盒开启后自动随机挑一个道具\n与手动挑选同一条链路，服务端发放", "Auto party item\npicks a random piece for you once the box opens\nsame path as picking by hand; the server hands it over");
        SRApi.LocKey("Level", "Auto Party Item Delay", "自动道具延迟", null);
        SRApi.LocDesc("Level", "Auto Party Item Delay", "自动道具延迟\n开箱后等多少秒再挑\n范围 0-3，步进 0.1", "Auto item delay\nseconds to wait after the box opens\nrange 0-3 in steps of 0.1");
        SRApi.LocKey("Level", "Auto Party Item Skip Bomb", "跳过炸弹", null);
        SRApi.LocDesc("Level", "Auto Party Item Skip Bomb", "跳过炸弹\n自动挑道具时排除派对盒里的炸弹\n关闭后炸弹也可能被自动挑中", "Skip bombs\nexcludes party-box bombs from the random pick\nwhen off a bomb may be picked");
    }

    public void Initialize(IFeatureHost plugin) {
        try {
            SelfReg();
            _enabled = plugin.Config.Bind("Level", "Auto Party Item", false,
                "自动道具：派对盒开启后自动随机挑一个道具（与手动挑选同一条链路）。");
            _delay = plugin.Config.Bind("Level", "Auto Party Item Delay", 0.5f,
                new ConfigDescription("自动道具延迟（0-3 秒，步进 0.1）：开箱后等多久再自动挑。", new AcceptableValueRange<float>(0f, 3f)));
            _skipBomb = plugin.Config.Bind("Level", "Auto Party Item Skip Bomb", false,
                "跳过炸弹：自动挑道具时不挑派对盒里的炸弹。");
            SRApi.RegisterTick("派对盒-自动道具", Tick);
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/初始化", __ex); }
    }

    private static void Tick() {
        try {
            // 挑选已改为一次性下发，不需要逐帧推进悬停状态。
            // 先判总开关：关闭时不做每帧全场景查找。
            if (_enabled == null || !_enabled.Value) { _armedAt = -1f; return; }
            if (!SRApi.GateMaster) return;
            // 开箱序号：光标从"未挑选"变成"正在挑选"就是新的一次开箱。
            PartyPickCursor seen = LocalCursor();
            if (seen != null && !_cursorWasPicking) { _openSeq++; _armedAt = -1f; _hpAttempts = 0; }
            _cursorWasPicking = seen != null;
            if (Time.unscaledTime < _nextAt) return;
            // 派对盒正在刷新/关闭时本帧不动作，避免对已销毁的旧盒取道具。
            if (Time.unscaledTime < LevelPartyBox.BoxBusyUntil) { _armedAt = -1f; return; }
            _nextAt = Time.unscaledTime + 0.15f;   // 轮询节流：不抢游戏自己的逻辑，也不每帧全场景找

            PartyPickCursor cur = seen;
            if (cur == null) { _armedAt = -1f; return; }
            // 光标处于挑选态就说明盒子开着：不强依赖 PartyBox 实例（跨房/重建时可能取不到或状态过期）。
            PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
            if (_pickedSeq == _openSeq) return;      // 本次开箱已经自动挑过
            if (_armedAt < 0f) { _armedAt = Time.unscaledTime; return; }
            float delay = _delay != null ? Mathf.Clamp(_delay.Value, 0f, 3f) : 0.5f;
            if (Time.unscaledTime - _armedAt < delay) return;

            List<PickableBlock> pieces = ReadPieces(pb);
            if (pieces == null || pieces.Count == 0) return;
            PickableBlock target = Choose(pb, pieces);
            if (target == null) return;
            // 仅在此标记本次已挑，成败由 TickHoverPick 验收阶段判定（避免重复日志）。
            if (DoPick(cur, target)) _pickedSeq = _openSeq;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/轮询", __ex); }
    }

    // 本地玩家的派对盒光标：有权威、已启用、正处于挑选状态。
    private static PartyPickCursor LocalCursor() {
        try {
            PartyPickCursor[] cursors = SRApi.FrameObjects<PartyPickCursor>();   // 按帧缓存，避免每帧全场景 FindObjectsOfType
            if (cursors == null) return null;
            for (int i = 0; i < cursors.Length; i++) {
                PartyPickCursor c = cursors[i];
                if (c == null || !c.hasAuthority || !c.Enabled || !c.Picking) continue;
                return c;
            }
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/查找光标", __ex); }
        return null;
    }

    // 候选道具：优先读 PartyBox.pieces；反射失败时退回"场景里在派对盒内的道具"。
    private static List<PickableBlock> ReadPieces(PartyBox pb) {
        try {
            if (_fPieces == null) _fPieces = SRApi.Ref.Field(typeof(PartyBox), "pieces", "派对盒自动道具（候选列表）");
            List<PickableBlock> list = _fPieces != null ? _fPieces.GetValue(pb) as List<PickableBlock> : null;
            if (list != null && list.Count > 0) return list;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/读取候选", __ex); }
        try {
            PickableBlock[] all = SRApi.FrameObjects<PickableBlock>();
            if (all == null) return null;
            List<PickableBlock> scan = new List<PickableBlock>();
            for (int i = 0; i < all.Length; i++) {
                PickableBlock b = all[i];
                if (b != null && b.InPartybox) scan.Add(b);
            }
            return scan;
        } catch (Exception __sc) { SRApi.Guard.Log("自动道具/扫描候选", __sc); return null; }
    }

    private static PickableBlock Choose(PartyBox pb, List<PickableBlock> pieces) {
        bool skipBomb = _skipBomb == null || _skipBomb.Value;
        List<PickableBlock> cand = new List<PickableBlock>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++) {
            PickableBlock p = pieces[i];
            if (p == null) continue;
            if (skipBomb && IsBomb(pb, p)) continue;
            cand.Add(p);
        }
        if (cand.Count == 0) return null;   // 只剩炸弹且选择了跳过：放弃本次自动挑选
        return cand[UnityEngine.Random.Range(0, cand.Count)];
    }







    private static bool IsBomb(PartyBox pb, PickableBlock p) {
        try {
            if (pb.BombPrefab == null || p.placeablePrefab == null) return false;
            for (int i = 0; i < pb.BombPrefab.Length; i++) {
                if (pb.BombPrefab[i] != null && pb.BombPrefab[i] == p.placeablePrefab) return true;
            }
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/炸弹判定", __ex); }
        return false;
    }

    // 走游戏 tryPickPiece 链路：Picking=false → 记 NetworkpickedPiece →（有权威才）发
    // PiecePicked → Freeze → Disable()。顺序不可变，否则房主端判为无效挑选。
    // 故光标须先悬停在道具上：游戏靠碰撞体算 hoveredPick，并从中取 netIdValue。
    // 直接走游戏自己的挑选结果链路：写 NetworkpickedPiece + 发 MsgPiecePicked + 冻结/关闭光标。
    // 这与手动点选后 tryPickPiece 的做法一致，不依赖 hoveredPick（物理触发）或反射写悬停字段，
    // 房主与房客都能用。
    private static bool DoPick(PartyPickCursor cur, PickableBlock target) {
        try {
            if (cur == null || target == null) return false;
            var nm = UnityEngine.Networking.NetworkManager.singleton;
            if (nm == null || nm.client == null) return false;
            cur.Picking = false;
            try {
                cur.NetworkpickedPiece = UnityEngine.Networking.ClientScene.FindLocalObject(
                    new UnityEngine.Networking.NetworkInstanceId(target.netIdValue));
            } catch (Exception __np) { SRApi.Guard.Log("自动道具/写选中项", __np); }
            if (cur.hasAuthority) {
                MsgPiecePicked msg = new MsgPiecePicked();
                msg.PickableNetID = target.netIdValue;
                msg.PlayerNumber = cur.networkNumber;
                nm.client.Send(NetMsgTypes.PiecePicked, msg);
            }
            try { cur.Freeze(); cur.Disable(); } catch (Exception __fd) { SRApi.Guard.Log("自动道具/关闭光标", __fd); }
            SRApi.LogInfo("[自动道具] 已挑选：" + target.name);
            return true;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/挑选", __ex); return false; }
    }

    // 每帧推进：优先直接写 hoveredPick 并同栈启动协程，失败才复刻手动挑选。
    // 写完必须同栈启动协程：中间跑物理帧会触发 OnTriggerExit2D 清掉 hoveredPick。
    private static void TickHoverPick() {
        try {
            if (_hpStage == 0 || _hpCur == null || _hpTarget == null) return;
            if (Time.unscaledTime < _hpAt) return;

            if (_hpStage == 1) {
                // 直接选择：清残留 → 写悬停 → 同栈启动协程。
                bool started = false;
                try { _hpCur.NetworkpickedPiece = null; } catch (Exception __cp) { SRApi.Guard.Log("自动道具/清挑选记录", __cp); }
                System.Reflection.FieldInfo hp = null;
                try { hp = SRApi.Ref.Field(typeof(PartyPickCursor), "hoveredPick", "派对盒自动道具（直接选择）"); }
                catch (Exception __hf) { SRApi.Guard.Log("自动道具/取悬停字段", __hf); }
                if (hp != null) {
                    try { hp.SetValue(_hpCur, _hpTarget); } catch (Exception __hs) { SRApi.Guard.Log("自动道具/写悬停字段", __hs); }
                    started = StartPickCoroutine();
                }
                _hpStage = started ? 3 : 2;
                _hpAt = started ? Time.unscaledTime + 0.45f : Time.unscaledTime + 0.12f;
                return;
            }

            if (_hpStage == 2) {
                // 兜底：复刻手动挑选 —— 把光标挪到道具上，等物理帧把 hoveredPick 填上再启动协程。
                if (!_hpMoved && _hpCur != null && _hpTarget != null) {
                    _hpSavedPos = _hpCur.transform.position;
                    _hpMoved = true;
                    Vector3 p = _hpTarget.transform.position;
                    _hpCur.transform.position = new Vector3(p.x, p.y + 0.15f, p.z);
                    _hpAt = Time.unscaledTime + 0.12f;
                    return;
                }
                try { _hpCur.NetworkpickedPiece = null; } catch (Exception __cp2) { SRApi.Guard.Log("自动道具/清挑选记录", __cp2); }
                bool ok = StartPickCoroutine();
                _hpStage = 3;
                _hpAt = Time.unscaledTime + 0.45f;
                if (!ok) { _hpStage = 0; _hpCur = null; _hpTarget = null; }
                return;
            }

            if (_hpStage == 3) {
                bool ok = false;
                try {
                    PartyPickCursor c0 = _hpCur;
                    if (c0 != null) ok = !c0.Picking || c0.Pick != null;   // 协程成功会把 Picking 置 false
                } catch (Exception __ok) { SRApi.Guard.Log("自动道具/确认挑选结果", __ok); }
                if (ok) {
                    _hpAttempts = 0;
                    SRApi.LogInfo("[自动道具] 已挑选：" + (_hpTarget != null ? _hpTarget.name : "?"));
                } else if (_hpAttempts < 2) {
                    _hpAttempts++;
                    _pickedSeq = -1;                 // 允许本箱再挑一次
                    _armedAt = Time.unscaledTime;    // 重新计时延迟
                }
                RestoreCursorPos();
                try {
                    System.Reflection.FieldInfo hp2 = SRApi.Ref.Field(typeof(PartyPickCursor), "hoveredPick", "派对盒自动道具（清理伪造悬停）");
                    if (hp2 != null && _hpCur != null) hp2.SetValue(_hpCur, null);
                } catch (Exception __hc) { SRApi.Guard.Log("自动道具/清理悬停字段", __hc); }
                _hpStage = 0; _hpCur = null; _hpTarget = null;
            }
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/挑选推进", __ex); }
    }

    // 启动游戏自己的 tryPickPiece 协程（房主端只认这条链路）。
    private static bool StartPickCoroutine() {
        try {
            System.Reflection.MethodInfo mi = null;
            try { mi = SRApi.Ref.Method(typeof(PartyPickCursor), "tryPickPiece", "派对盒自动道具（走游戏挑选协程）"); }
            catch (Exception __rf) { SRApi.Guard.Log("自动道具/取挑选方法", __rf); }
            if (mi == null) { FallbackSendPick(_hpCur, _hpTarget); return true; }
            System.Collections.IEnumerator co = mi.Invoke(_hpCur, null) as System.Collections.IEnumerator;
            if (co == null) return false;
            _hpCur.StartCoroutine(co);
            return true;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/启动协程", __ex); return false; }
    }

    private static void RestoreCursorPos() {
        if (!_hpMoved || _hpCur == null) { _hpMoved = false; return; }
        try { _hpCur.transform.position = _hpSavedPos; } catch { /* 静默：光标可能已销毁 */ }
        _hpMoved = false;
    }

    // 兜底：直接发 PiecePicked，仅当反射拿不到协程时。
    private static void FallbackSendPick(PartyPickCursor cur, PickableBlock target) {
        try {
            if (cur == null || target == null) return;
            if (UnityEngine.Networking.NetworkManager.singleton == null || UnityEngine.Networking.NetworkManager.singleton.client == null) return;
            cur.Picking = false;
            cur.NetworkpickedPiece = UnityEngine.Networking.ClientScene.FindLocalObject(new UnityEngine.Networking.NetworkInstanceId(target.netIdValue));
            if (cur.hasAuthority) {
                MsgPiecePicked msg = new MsgPiecePicked();
                msg.PickableNetID = target.netIdValue;
                msg.PlayerNumber = cur.networkNumber;
                UnityEngine.Networking.NetworkManager.singleton.client.Send(NetMsgTypes.PiecePicked, msg);
            }
            cur.Freeze();
            cur.Disable();
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/兜底下发", __ex); }
    }
}
}
