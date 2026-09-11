using System;

namespace SR_UCH.Gating
{
    // ============================================================
    // 门控契约（T1）：把“功能限制”从散落的手写 if 提升为一等公民模型。
    // 硬约束：BepInEx 5.4 / Harmony 2 / Unity 老 Mono → 只允许 C# 7.3 语法
    //（禁止 switch 表达式、可空引用类型、using 声明、默认接口方法等）。
    // ============================================================

    // 限制判定的三态结果：
    //  Allow  = 放行
    //  Deny   = 拒绝，并给出可本地化的原因（UI 可灰显 + tooltip 提示）
    //  Silent = 静默拒绝（不灰显、不提示；例如“UI 打开时不抢输入”属于正常控制流）
    // 之所以要区分 Deny / Silent：历史上的 30 处手写守卫里，既有限制也有正常控制流，
    // 一律当成“拒绝并提示”会让 UI 出现无意义的告警。
    public enum Verdict
    {
        Allow = 0,
        Deny = 1,
        Silent = 2
    }

    // 一个限制声明要投影到哪些执行点。
    // 用 [Flags] + 单属性的目的，是消灭缺陷 1（同一概念在 5 处各写一遍）：
    // 声明一次 AppliesTo，框架自动投影到 能力门 / 可见门 / 可变门。
    [Flags]
    public enum GateScope
    {
        None = 0,
        Capability = 1 << 0, // 补丁/业务逻辑是否执行
        Visibility = 1 << 1, // 栏目/条目是否显示（灰显）
        Mutability = 1 << 2, // 配置值是否允许被改写
        All = Capability | Visibility | Mutability
    }

    // 进度解锁组（A/B）。用位标志，方便 GateContext 一次性缓存解锁状态。
    [Flags]
    public enum ProgressGroup
    {
        None = 0,
        A = 1 << 0,
        B = 1 << 1
    }

    // 求值结果：是否放行 + 拒绝原因（本地化键 + 参数）。
    // 只读 struct：每帧可能构造多次，避免 GC（该项目历史上对每帧分配很敏感）。
    public readonly struct GateResult
    {
        public readonly Verdict Verdict;
        public readonly string ReasonKey;   // 本地化键；null 表示无原因
        public readonly object[] Args;      // 本地化占位参数

        public bool Allowed { get { return Verdict == Verdict.Allow; } }
        public bool IsDenied { get { return Verdict == Verdict.Deny; } }
        public bool IsSilent { get { return Verdict == Verdict.Silent; } }

        public GateResult(Verdict verdict, string reasonKey, object[] args)
        {
            Verdict = verdict;
            ReasonKey = reasonKey;
            Args = args;
        }

        public static readonly GateResult Ok = new GateResult(Verdict.Allow, null, null);

        public static GateResult Deny(string reasonKey, params object[] args)
        {
            return new GateResult(Verdict.Deny, reasonKey, args);
        }

        public static GateResult Silence(string reasonKey, params object[] args)
        {
            return new GateResult(Verdict.Silent, reasonKey, args);
        }
    }

    // 门控上下文快照：GateService 每帧刷新一次，所有限制读同一份 → 同帧一致。
    // 这同时解决缺陷 2（进度解锁既有每帧拉取、又有每 60 秒推送）与性能问题
    //（不再在 GameControl.Update 前缀里每帧读 StatTracker 存档）。
    public readonly struct GateContext
    {
        public readonly bool Master;        // 总开关 AllEnabled
        public readonly bool IsHost;        // 统一房主判定（取代 4 套实现）
        public readonly bool InTreehouse;   // 树屋大厅
        public readonly bool InMatch;       // 对局中
        public readonly bool UiCapturing;   // UI 正在吃输入（SR.UiOpen && SR.BlockInput）
        public readonly bool OverrideModes; // 无视模式限制 IgnoreModeLimit
        public readonly bool OverrideHost;  // 无视房主限制 IgnoreHostLimit（来自 EX）
        public readonly GameState.GameMode Mode;
        public readonly ProgressGroup Unlocked; // 已解锁的进度组

        public GateContext(bool master, bool isHost, bool inTreehouse, bool inMatch, bool uiCapturing,
                           bool overrideModes, bool overrideHost, GameState.GameMode mode, ProgressGroup unlocked)
        {
            Master = master;
            IsHost = isHost;
            InTreehouse = inTreehouse;
            InMatch = inMatch;
            UiCapturing = uiCapturing;
            OverrideModes = overrideModes;
            OverrideHost = overrideHost;
            Mode = mode;
            Unlocked = unlocked;
        }

        // 指定进度组是否已解锁（None 视为已解锁）
        public bool Has(ProgressGroup group)
        {
            return group == ProgressGroup.None || (Unlocked & group) == group;
        }
    }

    // 一个限制：声明它作用于哪些执行点、是否可被豁免、如何求值。
    public interface IRequirement
    {
        GateScope AppliesTo { get; }
        bool Overridable { get; }
        GateResult Evaluate(in GateContext ctx);
    }
}
