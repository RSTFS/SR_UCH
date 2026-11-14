// 通知中心的纯逻辑：分级关键词匹配 / 合并键归一化。无 Unity / BepInEx 依赖，单元测试直接覆盖。
using System;
using System.Text;

namespace SR_UCH.Tweaks {
    internal static class NotifyLogic {
        public static bool Contains(string s, string w) {
            return !string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(w)
                && s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 返回 0 普通 / 1 注意 / 2 重要：高优先级词优先，其次低优先级词，都没有算"一般反馈"。
        public static int Classify(string text, string tag, string[] high, string[] low) {
            string all = (text ?? "") + " " + (tag ?? "");
            if (high != null) for (int i = 0; i < high.Length; i++) if (Contains(all, high[i])) return 2;
            if (low != null) for (int i = 0; i < low.Length; i++) if (Contains(all, low[i])) return 0;
            return 1;
        }

        // 合并键归一化：去数字（倒计时会变）、压缩空白、转小写、截前 48 字。
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
