// 运行时规则的可测面：组合根关机顺序、换页状态转移、双轨 cutover 判定。
// 纯函数，不引用引擎；契约层与表驱动单测共用同一份判定。
using System;

namespace SR_UCH.Pure {
    internal static class RuntimeRules {

        // 关机必须按此顺序：先撤作用域，再清缓存与订阅，最后复位域。
        internal static readonly string[] ShutdownOrder = new string[] {
            "leases", "ref-cache", "subscriptions", "domains"
        };

        internal static bool OrderIsValid(string[] order) {
            if (order == null || order.Length != ShutdownOrder.Length) return false;
            for (int i = 0; i < order.Length; i++) {
                if (order[i] != ShutdownOrder[i]) return false;
            }
            return true;
        }

        // 换页：per-page 恢复上次位置，global 归零。与页级 ViewState 同源。
        internal static float PageTransition(float previous, float remembered, string policy) {
            if (policy == "global") return 0f;
            if (policy != "per-page") return 0f;
            return remembered >= 0f ? remembered : previous;
        }

        // 双轨 cutover：声明齐备与实机 A/B 两道都真才允许。
        internal static bool CanCutover(bool declarationReady, bool cutoverRequested, bool runtimeAbVerified) {
            return declarationReady && cutoverRequested && runtimeAbVerified;
        }

        // 关机不变量：已撤销数不能为负，域复位后计数归零。
        internal static bool ShutdownInvariant(int disposedLeases, int domainsReset) {
            return disposedLeases >= 0 && domainsReset == 4;
        }

        // 注入替身用：把服务名映射成可断言的键，不触碰任何全局状态。
        internal static string ServiceKey(string domain, string member) {
            if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(member)) return "";
            return domain + "." + member;
        }

        // 注入是否真正生效：替身写的值必须能被关机路径读回，而非只登记不用。
        internal static bool InjectionHonored(string primary, string injected) {
            return !string.IsNullOrEmpty(primary) && primary == injected;
        }

        // 关机报告：顺序、撤销数、域复位三项一起判定，供表驱动单测。
        internal static string ShutdownReport(bool orderValid, int disposed, int domainsReset) {
            return orderValid && ShutdownInvariant(disposed, domainsReset) ? "OK" : "REVIEW";
        }
    }
}
