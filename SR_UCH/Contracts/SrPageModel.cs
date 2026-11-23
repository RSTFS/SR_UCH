// <page-model:begin> 由 tools/spec_engine.py 重写，勿手改。
// 注意事项：页级滚动策略由 build/specs/ui.pages.json 派生；换页行为只看这里的常量。
namespace SR_UCH {
    internal static class SrPageModel {
        internal const string ScrollRestore = "per-page";
        internal const string WheelPositive = "up";
        internal static readonly string[] PageIds = new string[] {
            "Building Presets",
            "Destroy Blocks",
            "Player Tracker",
            "Quick Adjust",
            "Experiments",
            "Self-check",
            "Character",
            "Online",
            "Level",
            "build",
            "Chat",
            "Home",
            "品质光芒",
            "混乱牌组",
            "自由模式",
            "视觉辅助",
            "通知中心",
            "HUD",
        };
        internal static readonly string[] Policies = new string[] {
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
            "per-page",
        };
        internal static string PolicyFor(string pageKey) {
            if (string.IsNullOrEmpty(pageKey)) return ScrollRestore;
            for (int i = 0; i < PageIds.Length; i++) {
                if (pageKey.IndexOf(PageIds[i], System.StringComparison.OrdinalIgnoreCase) >= 0 && i < Policies.Length) return Policies[i];
            }
            return ScrollRestore;
        }
    }
}
// <page-model:end>
