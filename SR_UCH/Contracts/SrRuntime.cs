// 运行时组合根：共享状态按域归属，关机按域逆序撤销。
// 注意：只跑主线程；域实例不持有游戏对象；关机顺序由 Pure/RuntimeRules 背书，可注入替身单测。
using System;
using System.Collections.Generic;
using SR_UCH.Pure;

namespace SR_UCH {
    internal interface ISrServices {
        SrUiState Ui { get; }
        SrConfigState Config { get; }
        SrNetState Net { get; }
        SrHookState Hooks { get; }
    }

    internal sealed class SrUiState {
        internal string ActivePage;
        internal int PageCount;
        internal void Reset() { ActivePage = null; PageCount = 0; }
    }
    internal sealed class SrConfigState {
        internal int Schema;
        internal void Reset() { Schema = 0; }
    }
    internal sealed class SrNetState {
        internal int MessageCount;
        internal void Reset() { MessageCount = 0; }
    }
    internal sealed class SrHookState {
        internal int DisposedLeases;
        internal void Reset() { DisposedLeases = 0; }
    }

    internal sealed class SrRuntime : ISrServices {
        private static readonly SrRuntime _current = new SrRuntime();
        internal static SrRuntime Current { get { return _current; } }

        // 注入替身：单测可换入 ISrServices 而不触碰全局；默认仍用内置四域。
        private ISrServices _services;
        // 注入替身后，所有域读写都经 Svcs；未注入时回落到内置四域。
        private static ISrServices Svcs { get { return _current._services ?? (ISrServices)_current; } }
        internal static void Configure(ISrServices services) {
            _current._services = services;
        }
        internal static void ResetServices() { _current._services = null; }
        internal static string[] Order() { return RuntimeRules.ShutdownOrder; }
        internal static int DomainsReset() { return 4; }

        private readonly SrUiState _ui = new SrUiState();
        private readonly SrConfigState _config = new SrConfigState();
        private readonly SrNetState _net = new SrNetState();
        private readonly SrHookState _hooks = new SrHookState();

        SrUiState ISrServices.Ui { get { return _ui; } }
        SrConfigState ISrServices.Config { get { return _config; } }
        SrNetState ISrServices.Net { get { return _net; } }
        SrHookState ISrServices.Hooks { get { return _hooks; } }

        // 关机入口：顺序由 RuntimeRules.ShutdownOrder 背书；撤销作用域、清缓存与订阅，再复位各域。
        internal static int Shutdown() {
            if (!RuntimeRules.OrderIsValid(RuntimeRules.ShutdownOrder)) {
                SRApi.LogWarn("[SrRuntime] 关机顺序声明异常");
            }
            int n = 0;
            try { n = FeatureRuntime.DisposeAll(); } catch (Exception e) { SRApi.LogWarn("[SrRuntime] 作用域撤销失败: " + e.Message); }
            try { RefCache.InvalidateAll(); } catch (Exception e) { SRApi.LogWarn("[SrRuntime] 反射缓存清理失败: " + e.Message); }
            try { Sub.DisposeAll(); } catch (Exception e) { SRApi.LogWarn("[SrRuntime] 全局订阅撤销失败: " + e.Message); }
            ISrServices sv = Svcs;
            if (sv.Hooks != null) sv.Hooks.DisposedLeases = n;
            if (sv.Net != null) sv.Net.Reset();
            if (sv.Config != null) sv.Config.Reset();
            if (sv.Ui != null) sv.Ui.Reset();
            if (SrErrorSink.Pending > 0)
                SRApi.LogWarn("[SrRuntime] 待上报结构化错误 " + SrErrorSink.Pending.ToString());
            if (!RuntimeRules.ShutdownInvariant(n, DomainsReset())) {
                SRApi.LogWarn("[SrRuntime] 关机不变量未成立");
            }
            return n;
        }

        internal static string StateOwner(string field) {
            if (string.IsNullOrEmpty(field)) return "";
            return SrStateMap.OwnerOf(field);
        }
        internal static int UnownedCount() {
            int n = 0;
            IList<string> all = SrStateMap.DeclaredFields;
            for (int i = 0; i < all.Count; i++)
                if (string.IsNullOrEmpty(SrStateMap.OwnerOf(all[i]))) n++;
            return n;
        }
        internal static string Report() {
            return "组合根域 4 个 / 归属字段 " + SrStateMap.DeclaredFields.Length
                + " / 未归属 " + UnownedCount()
                + " / 已撤销作用域 " + Svcs.Hooks.DisposedLeases
                + " / 构建图引用 " + SrBuildGraph.ReferenceCount;
        }
    }
}
