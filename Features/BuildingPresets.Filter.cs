// 布局预设 · 筛选 / 排序 / 可见行集合。
// 从 BuildingPresets.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {
        private static List<PresetInfo> VisiblePresets() {
            // 签名驱动：只有数据 / 筛选 / 排序 / 语言 / 显示模式真正变化时才重算。
            // 同一帧内 OnGUI 的多次调用由 _sigFrame 折叠成一次签名计算。
            int sig = VisibleSignature();
            if (sig != _visSig) {
                _visSig = sig;
                _visSource.Filter = PassPresetFilter;
                _visSource.Sort = ComparePreset;
                long stamp = Time.frameCount;
                _visSource.Invalidate();
                _visSource.Refresh(_presets, stamp);
            }
            return _visSource.Rows;
        }

        // 覆盖所有会影响可见行内容的输入。漏掉任何一处都只会让列表"不刷新"，所以显式枚举，
        // 不依赖调用方记得 Invalidate。签名按帧缓存，避免每趟 OnGUI 都遍历一次。
        private static int VisibleSignature() {
            if (_sigFrame == Time.frameCount) return _sigCache;
            unchecked {
                int h = 17;
                h = h * 31 + _presets.Count;
                h = h * 31 + SortMode;
                h = h * 31 + (_search ?? "").GetHashCode();
                h = h * 31 + (_levelFilter ?? "").GetHashCode();
                h = h * 31 + (_modeFilter ?? "").GetHashCode();
                h = h * 31 + (_typeFilter ?? "").GetHashCode();
                h = h * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                h = h * 31 + (_levelShowCode ? 1 : 0);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null) { h = h * 31; continue; }
                    h = h * 31 + (p.Name ?? "").GetHashCode();
                    h = h * 31 + (p.Level ?? "").GetHashCode();
                    h = h * 31 + (p.Mode ?? "").GetHashCode();
                    h = h * 31 + p.TimeUtc.GetHashCode();
                    h = h * 31 + (p.Ok ? 1 : 0);
                    h = h * 31 + (p.ModsMode ?? "").GetHashCode();
                    h = h * 31 + (p.IsLocal ? 1 : 0);
                }
                _sigFrame = Time.frameCount;
                _sigCache = h;
                return h;
            }
        }

        // 仅筛选条件（不含数据）的签名：变化时复位滚动位置与二次确认态。
        private static int FilterSignature() {
            unchecked {
                int h = 7;
                h = h * 31 + (_search ?? "").GetHashCode();
                h = h * 31 + (_levelFilter ?? "").GetHashCode();
                h = h * 31 + (_modeFilter ?? "").GetHashCode();
                h = h * 31 + (_typeFilter ?? "").GetHashCode();
                h = h * 31 + SortMode;
                return h;
            }
        }

        // 筛选下拉选项的签名：只依赖预设集合里的 Level/Mode 与当前语言。
        // 两个下拉同帧各调一次；这里一次遍历同时算出两份签名并按帧缓存。
        private static void EnsureOptionSignatures() {
            if (_optSigFrame == Time.frameCount) return;
            unchecked {
                int hl = 3, hm = 5;
                hl = hl * 31 + _presets.Count; hm = hm * 31 + _presets.Count;
                hl = hl * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                hm = hm * 31 + (SRApi.Ctl.LangEn ? 1 : 0);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null) { hl *= 31; hm *= 31; continue; }
                    hl = hl * 31 + (p.Level ?? "").GetHashCode();
                    hm = hm * 31 + (p.Mode ?? "").GetHashCode();
                }
                _optSigLevel = hl; _optSigMode = hm;
                _optSigFrame = Time.frameCount;
            }
        }

        private static int FilterOptionsSignature(bool level) {
            EnsureOptionSignatures();
            return level ? _optSigLevel : _optSigMode;
        }

        // 过滤条件（原 VisiblePresets 内的判断，原样保留语义）。
        private static bool PassPresetFilter(PresetInfo p) {
            try {
                string q = (_search ?? "").Trim();
                if (string.Equals(_typeFilter, "preset", StringComparison.Ordinal) && p.IsLocal) return false;
                if (string.Equals(_typeFilter, "local", StringComparison.Ordinal) && !p.IsLocal) return false;
                if (!string.IsNullOrEmpty(_levelFilter)
                    && !string.Equals(p.Level, _levelFilter, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrEmpty(_modeFilter)
                    && !string.Equals(p.Mode, _modeFilter, StringComparison.OrdinalIgnoreCase)) return false;
                if (q.Length > 0) {
                    // 只按名称搜索（用户要求）：关卡/模式已有专门的下拉筛选，文本搜索只负责命名。
                    if (!ContainsText(p.Name, q)) return false;
                }
                return true;
            } catch { return false; }
        }

        private static bool ContainsText(string text, string query) {
            try { return text != null && query != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }
    }
}
