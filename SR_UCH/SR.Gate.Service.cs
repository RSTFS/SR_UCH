using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Gate（门控服务：每帧上下文快照 + 统一房主判定）====
//
// 目标（提示词 T2/T3 的基础设施）：
//  · 每帧刷新一份 GateContext 快照，所有限制读同一份 → 同帧一致（解决“拉取/推送混用”缺陷）。
//  · 统一房主判定。注意：原来的 4 处“房主判断”其实不是同一种语义，不能简单合并成一个：
//      - DestroyBlocks.IsHostNow：离线/无连接也算“本机是权威”（单机/本地对局要能删块）。
//      - SR.Reload / PartyBomb 用 NetworkServer.active：只是“有没有服务器可广播”，
//        离线时不能去写 SyncVar/广播。
//    因此拆成两个明确概念：IsHost（权威，含离线）与 HasServer（NetworkServer.active）。
//    这是有意的设计判断，避免把两种语义混成一个后引入离线写网络的错误。

        // ---- 房主判定 ----

        // 是否具备“本机即权威”的房主权限（含离线/无连接；单机也算）。
        // 取代：DestroyBlocks.IsHostNow。
        public static bool IsHost {
            get {
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (NetworkServer.active) return lm == null || lm.IsHost;
                    // 没有服务器在跑：本机就是权威（离线/本地对局）。
                    return lm == null || lm.client == null || !lm.client.isConnected;
                } catch { return false; }
            }
        }

        // 是否存在可广播的服务器（NetworkServer.active）。
        // 取代：SR.Reload / PartyBomb 里的直判 NetworkServer.active。
        public static bool HasServer {
            get { try { return NetworkServer.active; } catch { return false; } }
        }

        // ---- 每帧上下文快照 ----

        private static Gating.GateContext _gateCtx;
        private static float _gateProgRefreshAt = -1f;      // 进度缓存上次刷新时刻
        private static Gating.ProgressGroup _gateUnlocked;  // 已解锁进度组（缓存，避免每帧读存档）

        // 当前帧的门控快照（未刷新时是默认值，等价于“全关”，保守）。
        public static Gating.GateContext Gate { get { return _gateCtx; } }

        // 进度解锁缓存刷新间隔：存档变化不频繁，5 秒足够；关键是**不要在每帧路径读 StatTracker**
        //（原 DestroyBlocks.cs:188 在 GameControl.Update 前缀里每帧读存档，是已知性能缺陷）。
        private const float GateProgressRefreshSeconds = 5f;

        // 每帧刷新一次（由 SR.Tick 调用）。任何限制都应读这份快照，而不是各自重新求值。
        public static void RefreshGate() {
            try {
                bool master = AllEnabled;
                bool isHost = IsHost;
                bool treehouse = InTreehouseLobby();
                bool inMatch;
                try {
                    LobbyManager lm = LobbyManager.instance;
                    inMatch = !treehouse && lm != null && lm.CurrentGameController != null;
                } catch { inMatch = false; }
                bool uiCapturing = UiOpen && BlockInput;
                bool overrideModes = IgnoreModeLimit;
                bool overrideHost = false;
                try { overrideHost = ExRef.IgnoreHostLimit; } catch { }
                GameState.GameMode mode = GameState.GameMode.FREEPLAY;
                try { mode = GameSettings.GetInstance().GameMode; } catch { }

                float now = Time.unscaledTime;
                if (_gateProgRefreshAt < 0f || now - _gateProgRefreshAt >= GateProgressRefreshSeconds) {
                    _gateProgRefreshAt = now;
                    Gating.ProgressGroup unlocked = Gating.ProgressGroup.None;
                    if (Experiments.ProgressionDataReady()) {
                        if (Experiments.IsProgressionUnlocked()) unlocked |= Gating.ProgressGroup.A;
                        if (Experiments.IsProgressionUnlockedB()) unlocked |= Gating.ProgressGroup.B;
                    }
                    _gateUnlocked = unlocked;
                }

                _gateCtx = new Gating.GateContext(master, isHost, treehouse, inMatch, uiCapturing,
                    overrideModes, overrideHost, mode, _gateUnlocked);
            } catch { }
        }

        // 强制让下次 RefreshGate 重新读一次进度（例如刚解锁/存档切换后）。
        public static void InvalidateGateProgress() { _gateProgRefreshAt = -1f; }

        // ---- 求值入口 ----

        // 对单个限制求值。scope 用于决定该限制此刻是否在该执行点生效。
        public static Gating.GateResult EvaluateGate(Gating.IRequirement requirement, Gating.GateScope scope) {
            return EvaluateGate(requirement, scope, _gateCtx);
        }

        public static Gating.GateResult EvaluateGate(Gating.IRequirement requirement, Gating.GateScope scope, in Gating.GateContext ctx) {
            if (requirement == null) return Gating.GateResult.Ok;
            try {
                // 该限制不作用于本执行点 → 放行
                if ((requirement.AppliesTo & scope) == 0) return Gating.GateResult.Ok;
                return requirement.Evaluate(in ctx);
            } catch { return Gating.GateResult.Ok; }
        }

	}
}
