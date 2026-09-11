using System;
using SR_UCH.Gating;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：GateRequirements（内置限制：总开关 / 模式 / 房主）====
//
// 提示词 T4：把每帧路径上的手写守卫替换为内置 Requirement。
//  · Master：总开关，永不豁免。
//  · Mode  ：模式限制，**可被 IgnoreModeLimit 豁免**。
//           用户已确认：任何有模式限制的功能都要接入这个豁免
//           （历史上 DestroyBlocks / 重载关卡 / 快速切换·重试 漏接入，属缺陷 3）。
//  · Host  ：房主限制，可被 IgnoreHostLimit 豁免。
//
// 说明（务实落地，见提示词 5.3）：C# 7.3 下不做 IL 织入，因此除了 Requirement 类型本身，
// 另提供**零分配**的便捷查询（GateMaster / GateHost / GateModeAllows），供每帧路径直接调用；
// Requirement 类型保留给声明式注册与 T5 的 GateAudit 使用。

        // 模式位掩码：避免每帧用 params 数组（参数数组每次都分配）。
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

        // ---- 内置 Requirement 类型 ----

        internal sealed class MasterRequirement : IRequirement {
            public GateScope AppliesTo { get { return GateScope.Capability | GateScope.Visibility; } }
            public bool Overridable { get { return false; } }
            public GateResult Evaluate(in GateContext ctx) {
                return ctx.Master ? GateResult.Ok : GateResult.Deny("gate.master");
            }
        }

        internal sealed class ModeRequirement : IRequirement {
            private readonly ModeMask _allowed;
            public ModeRequirement(ModeMask allowed) { _allowed = allowed; }
            public GateScope AppliesTo { get { return GateScope.Capability | GateScope.Visibility; } }
            // 模式限制可被 IgnoreModeLimit 豁免（用户确认）。
            public bool Overridable { get { return true; } }
            public GateResult Evaluate(in GateContext ctx) {
                if (ctx.OverrideModes) return GateResult.Ok;
                return (MaskOf(ctx.Mode) & _allowed) != 0 ? GateResult.Ok : GateResult.Deny("gate.mode");
            }
        }

        internal sealed class HostRequirement : IRequirement {
            public GateScope AppliesTo { get { return GateScope.Capability | GateScope.Visibility; } }
            // 房主限制可被 IgnoreHostLimit 豁免（EX）。
            public bool Overridable { get { return true; } }
            public GateResult Evaluate(in GateContext ctx) {
                if (ctx.IsHost || ctx.OverrideHost) return GateResult.Ok;
                return GateResult.Deny("gate.host");
            }
        }

        // 单例（供声明式/审计使用；求值读的是每帧快照）
        public static readonly IRequirement MasterReq = new MasterRequirement();

        // ---- 零分配便捷查询（每帧路径直接用）----

        // 总开关读**实时**值（AllEnabled），而不是每帧快照：
        // 快照由 SR.Tick 刷新，而各补丁前缀可能在同一帧更早执行，读快照会有 1 帧延迟；
        // 总开关是“一键停用全部功能”的安全阀，必须立刻生效，故这里直接用权威字段。
        public static bool GateMaster { get { return AllEnabled; } }

        // 房主或“无视房主限制”放行。
        public static bool GateHost { get { return _gateCtx.IsHost || _gateCtx.OverrideHost; } }

        // 当前模式在允许集合内，或“无视模式限制”开启。
        public static bool GateModeAllows(ModeMask allowed) {
            GateContext ctx = _gateCtx;
            if (ctx.OverrideModes) return true;
            return (MaskOf(ctx.Mode) & allowed) != 0;
        }

	}
}
