// 关卡 · 自动道具（AutoPartyItem）：派对盒开启后自动挑一个道具。
// 从 Level.PartyBox.cs 拆出来：那个文件原来同时装着 4 个功能类。
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
    // "本次开箱"用**光标进入挑选态的次数**做序号，而不是派对盒实例 ID：
    // 派对盒对象在整个大厅里是同一个实例、一轮一轮反复复用，用实例 ID 记账会让
    // 第一轮挑过之后"已挑"标记永远为真 —— 表现就是"第一次可以拿，之后都不行"。
    private static int _openSeq;
    private static int _pickedSeq = -1;
    private static bool _cursorWasPicking;
    private static float _nextAt = -999f;
    // 悬停挑选流程：先把本地光标挪到目标道具上（让游戏自己算出 hoveredPick），
    // 下一拍再调用游戏自己的 tryPickPiece 协程 —— 房主端只认游戏这条链路。
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
            TickHoverPick();   // 挑选流程每帧推进（它自己有节流），不能被下面的 0.15s 轮询挡住
            // 先做便宜的总开关判断：关闭时不要每帧 FindObjectsOfType（原来在开关判断之前，功能关着也白扫）。
            if (_enabled == null || !_enabled.Value) { _armedAt = -1f; return; }
            if (!SRApi.GateMaster) return;
            // 开箱序号：光标从"未挑选"变成"正在挑选"就是新的一次开箱。
            PartyPickCursor seen = LocalCursor();
            if (seen != null && !_cursorWasPicking) { _openSeq++; _armedAt = -1f; _hpAttempts = 0; }
            _cursorWasPicking = seen != null;
            if (Time.unscaledTime < _nextAt) return;
            // 派对盒正在刷新/关闭：本帧不动作，避免对着已被销毁的旧盒子取道具（冲突修复）
            if (Time.unscaledTime < LevelPartyBox.BoxBusyUntil) { _armedAt = -1f; return; }
            _nextAt = Time.unscaledTime + 0.15f;   // 轮询节流：不抢游戏自己的逻辑，也不每帧全场景找

            PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
            if (pb == null) { _armedAt = -1f; return; }
            bool active = false;
            try { active = pb.IsStillActive; } catch (Exception __ac) { SRApi.Guard.Log("自动道具/盒状态", __ac); }
            if (!active) { _armedAt = -1f; return; }

            PartyPickCursor cur = seen;
            if (cur == null) { _armedAt = -1f; return; }
            if (_pickedSeq == _openSeq) return;      // 本次开箱已经自动挑过
            if (_armedAt < 0f) { _armedAt = Time.unscaledTime; return; }
            float delay = _delay != null ? Mathf.Clamp(_delay.Value, 0f, 3f) : 0.5f;
            if (Time.unscaledTime - _armedAt < delay) return;

            List<PickableBlock> pieces = ReadPieces(pb);
            if (pieces == null || pieces.Count == 0) return;
            PickableBlock target = Choose(pb, pieces);
            if (target == null) return;
            // 只在这里标记"本次已挑"，具体成没成由 TickHoverPick 的验收阶段判定
            // （避免两条日志一真一假、误以为跑了两遍）。
            if (DoPick(cur, target)) _pickedSeq = _openSeq;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/轮询", __ex); }
    }

    // 本地玩家自己的派对盒光标：有权威、已启用、正处于挑选状态。
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

    private static List<PickableBlock> ReadPieces(PartyBox pb) {
        try {
            if (_fPieces == null) _fPieces = SRApi.Ref.Field(typeof(PartyBox), "pieces", "派对盒自动道具（候选列表）");
            if (_fPieces == null) return null;
            return _fPieces.GetValue(pb) as List<PickableBlock>;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/读取候选", __ex); return null; }
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

    // 与游戏 PartyPickCursor.tryPickPiece 完全一致：记下选中的对象 -> 发 PiecePicked -> 冻结并关闭光标。
    // 严格照抄游戏 PartyPickCursor.tryPickPiece 的**每一步**（顺序也要一致）：
    //   Picking=false → 记 NetworkpickedPiece → （有权威才）发 PiecePicked → Freeze → Disable()
    // 之前在房客端失效，就是因为少了 Picking=false 且用了不同的 Disable 重载：
    // 房主端的校验只认"光标已结束挑选"的状态，状态不对就当作无效挑选丢掉。
    // 走游戏自己的挑选链路：光标必须先"悬停"在道具上（游戏靠碰撞体算 hoveredPick，
    // 并从它取 netIdValue）。直接发 PiecePicked 在房客端会被房主判为无效。
    private static bool DoPick(PartyPickCursor cur, PickableBlock target) {
        try {
            if (cur == null || target == null) return false;
            _hpCur = cur; _hpTarget = target; _hpStage = 1; _hpAt = Time.unscaledTime + 0.06f;
            return true;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/启动悬停挑选", __ex); return false; }
    }

    // 每帧推进挑选流程。优先"直接选择"（把目标写进 hoveredPick 后**同一瞬间**启动协程，
    // 中间不留任何等待，游戏没机会把它清掉）；失败才退回复刻手动挑选（挪光标让物理自己算悬停）。
    //
    // 以前"直接写字段"不生效的原因：写完等了一拍再启动协程，中间跑了一个物理帧，
    // 光标没压在道具上 → OnTriggerExit2D → ClearHoverPick 把字段清成了 null。
    private static void TickHoverPick() {
        try {
            if (_hpStage == 0 || _hpCur == null || _hpTarget == null) return;
            if (Time.unscaledTime < _hpAt) return;

            if (_hpStage == 1) {
                // 直接选择：清残留 → 写悬停 → 立刻启动协程（同一调用栈内）。
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

    // 兜底：与以前相同的直接发消息（仅当反射拿不到协程时）
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
