// 通知中心纯逻辑：关键词分级与合并键归一化；无 Unity 依赖，单测直接覆盖。
using System;
using System.Text;

namespace SR_UCH.Tweaks {
    internal static class NotifyLogic {
        public static bool Contains(string s, string w) {
            return !string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(w)
                && s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 命中高优先级词返回 2，命中低优先级词返回 0，都不命中返回 1。
        public static int Classify(string text, string tag, string[] high, string[] low) {
            string all = (text ?? "") + " " + (tag ?? "");
            if (high != null) for (int i = 0; i < high.Length; i++) if (Contains(all, high[i])) return 2;
            if (low != null) for (int i = 0; i < low.Length; i++) if (Contains(all, low[i])) return 0;
            return 1;
        }

        public static string Norm(string s) {
            StringBuilder sb = new StringBuilder(48);
            s = s ?? "";
            for (int i = 0; i < s.Length && sb.Length < 48; i++) {
                char c = s[i];
                if (char.IsDigit(c)) continue;
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
