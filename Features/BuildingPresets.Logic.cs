// 布局预设的筛选纯逻辑：无 Unity / BepInEx 依赖，单元测试直接覆盖。
using System;

namespace SR_UCH.Tweaks {
    internal static class PresetFilterLogic {
        // typeFilter: "" 全部 / "preset" 仅自存预设 / "local" 仅本地存档；level/mode 为空 = 不限；search 只匹配名称。
        public static bool Pass(string name, string level, string mode, bool isLocal,
                                string typeFilter, string levelFilter, string modeFilter, string search) {
            string t = typeFilter ?? "";
            if (string.Equals(t, "preset", StringComparison.Ordinal) && isLocal) return false;
            if (string.Equals(t, "local", StringComparison.Ordinal) && !isLocal) return false;
            if (!string.IsNullOrEmpty(levelFilter) && !string.Equals(level ?? "", levelFilter, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(modeFilter) && !string.Equals(mode ?? "", modeFilter, StringComparison.OrdinalIgnoreCase)) return false;
            string q = (search ?? "").Trim();
            if (q.Length > 0 && !ContainsText(name, q)) return false;
            return true;
        }

        public static bool ContainsText(string hay, string needle) {
            if (string.IsNullOrEmpty(needle)) return true;
            return !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
