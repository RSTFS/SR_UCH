// 注意事项：预设 XML 的四层防护，纯字符串判定；文件 I/O 由调用方负责。
// 根节点与运行时一致：布局预设 XML 的根是 <scene>，由 BuildingPresets 的导入校验背书。
using System;
using System.IO;

namespace SR_UCH.Pure {
    internal static class PresetXmlPolicy {
        internal const long MaxBytes = 4L * 1024 * 1024;
        internal const int MaxPieces = 3000;
        internal const string RootName = "scene";

        internal static bool IsUnderRoot(string root, string candidate) {
            if (root == null || candidate == null) return false;
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string c;
            try { c = Path.GetFullPath(candidate); } catch { return false; }
            return c.Equals(r, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool SizeOk(long bytes) { return bytes >= 0 && bytes <= MaxBytes; }
        internal static bool ForbidsDtd(string text) {
            return text != null && !text.Contains("<!DOCTYPE") && !text.Contains("<!ENTITY");
        }
        // 严格取第一个开始标签名：跳过 XML 声明、注释与处理指令，避免
        // "<sceneFake>" 或注释里出现 "<scene" 时被误判为合法根。
        internal static bool RootOk(string text) {
            if (text == null) return false;
            int i = 0, n = text.Length;
            while (i < n) {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) return false;
                if (text[i] != '<') return false;
                if (i + 1 < n && text[i + 1] == '?') { int e = text.IndexOf("?>", i + 2); if (e < 0) return false; i = e + 2; continue; }
                if (i + 3 < n && text.Substring(i, 4) == "<!--") { int e = text.IndexOf("-->", i + 4); if (e < 0) return false; i = e + 3; continue; }
                break;
            }
            if (i >= n || text[i] != '<') return false;
            int j = i + 1;
            while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '-' || text[j] == ':')) j++;
            string root = text.Substring(i + 1, j - i - 1);
            if (root != RootName) return false;
            return j >= n || char.IsWhiteSpace(text[j]) || text[j] == '/' || text[j] == '>';
        }
        internal static bool All(string root, string candidate, long bytes, string text) {
            return IsUnderRoot(root, candidate) && SizeOk(bytes) && ForbidsDtd(text) && RootOk(text);
        }
    }
}
