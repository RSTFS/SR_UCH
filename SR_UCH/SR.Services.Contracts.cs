using System;
using SR_UCH.Gating;

namespace SR_UCH.Services
{
    // ============================================================
    // 依赖倒置契约（T1）：功能只认这些接口，不直接调 UnityEngine / 游戏静态。
    // 目的：让逻辑可替换、可审计；也为后续 T7 分层重构预留稳定边界。
    // 与门控契约一样，本文件是纯新增，零风险、不动既有代码。
    // ============================================================

    // 日志（统一带功能名前缀，替代散落的 MainPlugin.ModLogger.LogXxx）
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    // 本地化（替代散落的 SR.T(zh, en)）
    public interface ILoc
    {
        string T(string zh, string en);
    }

    // 时间（替代直接读 UnityEngine.Time；便于测试与统一时间源）
    public interface IClock
    {
        float UnscaledTime { get; }
        float DeltaTime { get; }
    }

    // 进度解锁（读缓存，不每帧读存档）
    public interface IProgression
    {
        bool DataReady { get; }
        bool IsUnlocked(ProgressGroup group);
    }

    // 网络/房主判定（统一 4 套实现：DestroyBlocks.IsHostNow / Experiments.IsHostExp /
    // PartyBomb 直判 NetworkServer.active / SR.Reload 直判 NetworkServer.active）
    public interface INetService
    {
        bool IsHost { get; }
        bool InMatch { get; }
    }

    // 玩家查询（帧缓存；避免每物理帧 ToDictionary/ToArray 的分配）
    public interface IPlayerQuery
    {
        int Count { get; }
    }

    // 相机服务（把每帧被应用 4 次的相机逻辑收敛到 1-2 次）
    public interface ICameraService
    {
    }

    // 事件总线（功能间解耦；T7 视需要接入）
    public interface IEventBus
    {
    }

    // 配置服务（声明式配置 + 门控投影）
    public interface IConfigService
    {
    }

    // 门控服务：每帧快照 + 求值入口
    public interface IGateService
    {
        GateContext Context { get; }
        GateResult Evaluate(IRequirement requirement, GateScope scope, in GateContext ctx);
    }

    // 功能上下文：功能通过它拿到所有能力，而不是 static 上帝类 SR。
    public interface IModContext
    {
        ILog Log { get; }
        ILoc Loc { get; }
        IClock Clock { get; }
        IProgression Progression { get; }
        INetService Net { get; }
        IPlayerQuery Players { get; }
        ICameraService Camera { get; }
        IGateService Gate { get; }
        IEventBus Events { get; }
        IConfigService Config { get; }
    }
}
