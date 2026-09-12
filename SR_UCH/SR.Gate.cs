using System;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

// ==== 分区：Gate（门控：每帧上下文快照 + 统一房主判定 + 内置模式豁免 + 启动自检）====
// 为什么删掉原来的「限制声明抽象层」（声明式 Requirement + 求值结果/作用域/判定三态那套类型）：
// 那一层是给「进度解锁」建的，而进度解锁功能已整体删除 → 该层全项目零调用（grep 核实），属纯死代码。
// 注意：删掉它**不影响豁免机制** —— 无视模式限制 / 无视房主限制本来就内建在
// GateModeAllows / GateHost 里（读 GateContext 的 OverrideModes / OverrideHost），不依赖抽象层。

namespace SR_UCH.Tweaks {
public partial class SR {

        // 门控上下文快照：GateService 每帧刷新一次，所有限制读同一份 → 同帧一致。
        public readonly struct GateContext
        {
            public readonly bool Master;        // 总开关 AllEnabled
            public readonly bool IsHost;        // 统一房主判定
            public readonly bool InTreehouse;   // 树屋大厅
            public readonly bool InMatch;       // 对局中
            public readonly bool UiCapturing;   // UI 正在吃输入（SR.UiOpen && SR.BlockInput）
            public readonly bool OverrideModes; // 无视模式限制 IgnoreModeLimit
            public readonly bool OverrideHost;  // 无视房主限制 IgnoreHostLimit（来自 EX）
            public readonly GameState.GameMode Mode;
    
            public GateContext(bool master, bool isHost, bool inTreehouse, bool inMatch, bool uiCapturing,
                               bool overrideModes, bool overrideHost, GameState.GameMode mode)
            {
                Master = master;
                IsHost = isHost;
                InTreehouse = inTreehouse;
                InMatch = inMatch;
                UiCapturing = uiCapturing;
                OverrideModes = overrideModes;
                OverrideHost = overrideHost;
                Mode = mode;
            }
        }

// ==== 分区：GateRequirements（内置限制：总开关 / 模式 / 房主）====
// Master 永不豁免；Mode 可被 IgnoreModeLimit 豁免（凡有模式限制的功能都要接入）；Host 可被 IgnoreHostLimit 豁免。
// 另提供零分配便捷查询 GateMaster / GateHost / GateModeAllows 供每帧路径调用；Requirement 类型留给声明式注册与审计。

        // 模式位掩码：避免每帧用 params 数组（每次调用都会分配）。
        [Flags]
        public enum ModeMask {
            None = 0,
            Freeplay = 1 << 0,
            Party = 1 << 1,
            Creative = 1 << 2,
            Challenge = 1 << 3
        }

        public static ModeMask MaskOf(GameState.GameMode mode) {
            switch (mode) {
                case GameState.GameMode.FREEPLAY: return ModeMask.Freeplay;
                case GameState.GameMode.PARTY: return ModeMask.Party;
                case GameState.GameMode.CREATIVE: return ModeMask.Creative;
                case GameState.GameMode.CHALLENGE: return ModeMask.Challenge;
                default: return ModeMask.None;
            }
        }

        // ---- 零分配便捷查询（每帧路径直接用）----

        // 总开关读实时值（AllEnabled）而非每帧快照：补丁前缀可能在同一帧更早执行，读快照有 1 帧延迟，
        // 而总开关是“一键停用全部功能”的安全阀，必须立刻生效。
        public static bool GateMaster { get { return AllEnabled; } }

        public static bool GateHost { get { return _gateCtx.IsHost || _gateCtx.OverrideHost; } }

        public static bool GateModeAllows(ModeMask allowed) {
            GateContext ctx = _gateCtx;
            if (ctx.OverrideModes) return true;
            return (MaskOf(ctx.Mode) & allowed) != 0;
        }

// ==== 分区：Gate（门控服务：每帧上下文快照 + 统一房主判定）====
// 每帧刷新一份 GateContext 快照，所有限制读同一份 → 同帧一致（避免拉取/推送混用）。
// 房主判定拆成两个语义不同的概念：IsHost（本机即权威，含离线/单机）与 HasServer（NetworkServer.active，
// 即"有服务器可广播"）；合并会让离线场景误去写 SyncVar/广播。

        // ---- 房主判定 ----

        // 是否具备“本机即权威”的房主权限（含离线/无连接；单机也算）。
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
        public static bool HasServer {
            get { try { return NetworkServer.active; } catch { return false; } }
        }

        // ---- 每帧上下文快照 ----

        private static GateContext _gateCtx;

        // 当前帧的门控快照（未刷新时是默认值，等价于“全关”，保守）。
        public static GateContext Gate { get { return _gateCtx; } }

        // 每帧刷新一次（由 SR.Tick 调用）。任何限制都应读这份快照，而不是各自重新求值。
        public static void RefreshGate() {
            try {
                bool master = AllEnabled;
                bool isHost = IsHost;
                bool treehouse = Freeplay.InTreehouseLobby();
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

                _gateCtx = new GateContext(master, isHost, treehouse, inMatch, uiCapturing,
                    overrideModes, overrideHost, mode);
            } catch { }
        }

        // ---- 求值入口 ----

// 补丁自检基线：经 Harmony 运行时补丁登记表（GetAllPatchedMethods / GetPatchInfo）
// 列出本程序集实际打上的补丁目标，并标出挂在每帧方法（Update/FixedUpdate/LateUpdate/OnPreCull 等）上的补丁。

        public static class GateAudit {
            private static readonly HashSet<string> PerFrameMethods = new HashSet<string> {
                "Update", "FixedUpdate", "LateUpdate", "OnPreCull", "OnPreRender", "OnPostRender", "OnGUI"
            };

            private static bool _ran;

            public static void Run() {
                if (_ran) return; //只跑一次
                _ran = true;
                try {
                    Assembly asm = typeof(SR).Assembly;
                    int total = 0;
                    List<string> perFrame = new List<string>();

                    foreach (MethodBase mb in Harmony.GetAllPatchedMethods()) {
                        Patches info;
                        try { info = Harmony.GetPatchInfo(mb); } catch { continue; }
                        if (info == null) continue;
                        if (!HasOurs(info.Prefixes, asm) && !HasOurs(info.Postfixes, asm) && !HasOurs(info.Transpilers, asm))
                            continue; //不是本模组打的补丁

                        total++;
                        if (mb != null && PerFrameMethods.Contains(mb.Name)) {
                            string owner = mb.DeclaringType != null ? mb.DeclaringType.Name : "?";
                            perFrame.Add(owner + "." + mb.Name);
                        }
                    }

                    MainPlugin.ModLogger.LogInfo("[GateAudit] SR_UCH 补丁目标方法 " + total + " 个；其中挂在每帧方法上的 " + perFrame.Count + " 个：");
                    for (int i = 0; i < perFrame.Count; i++) {
                        MainPlugin.ModLogger.LogInfo("[GateAudit]   " + perFrame[i]);
                    }
                } catch (Exception ex) {
                    try { MainPlugin.ModLogger.LogWarning("[GateAudit] 扫描失败: " + ex.Message); } catch { }
                }
            }

            private static bool HasOurs(IEnumerable<Patch> patches, Assembly asm) {
                if (patches == null) return false;
                foreach (Patch p in patches) {
                    try {
                        if (p == null || p.PatchMethod == null || p.PatchMethod.DeclaringType == null) continue;
                        if (p.PatchMethod.DeclaringType.Assembly == asm) return true;
                    } catch { }
                }
                return false;
            }
        }

	}
}

