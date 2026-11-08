// 布局预设 · 筛选 / 排序 / 可见行集合。
// 从 BuildingPresets.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
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


        // 筛选下拉的**唯一**实现：候选、标签、当前值读写、展开态全部由调用方给。
        // 以前三路筛选（关卡/模式/类型）各抄一份"找当前项 + ComboBox + 落值 + try/catch"，
        // 差异只有标签映射函数，重复了约 60 行。
        private static void RenderFilterCombo(string tag, string[] vals, string[] labels,
                Func<string> get, Action<string> set, ref bool open, string fallback, float width) {
            try {
                if (vals == null || labels == null || vals.Length == 0 || vals.Length != labels.Length) return;
                string curVal = get();
                if (!string.IsNullOrEmpty(curVal) && Array.IndexOf(vals, curVal) < 0) { set(fallback); curVal = fallback; }
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) {
                    if (string.Equals(vals[i], curVal, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                cur = Mathf.Clamp(cur, 0, labels.Length - 1);
                bool o = open;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref o, width);
                open = o;
                if (sel >= 0 && sel < vals.Length) set(vals[sel]);
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/筛选-" + tag, __ex); }
        }

        private static void RenderLevelFilterCombo() {
            try {
                List<string> vals = LevelFilterValues();
                if (_levelOptVals == null || _levelOptVals.Length != vals.Count) { _levelOptVals = vals.ToArray(); _levelOptLabels = null; }
                if (_levelOptLabels == null || _levelOptLabels.Length != vals.Count) {
                    string[] labels = new string[vals.Count];
                    for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : LevelDisplayNameForScene(vals[i]);
                    _levelOptLabels = labels;
                }
                RenderFilterCombo("关卡", _levelOptVals, _levelOptLabels,
                    delegate { return _levelFilter; }, delegate(string v) { _levelFilter = v; },
                    ref _levelFilterOpen, "", W(110));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/渲染关卡筛选", __ex); }
        }

        // 关卡筛选选项：第一项为空（全部），其余为当前预设里出现过的关卡场景名，按本地化关卡名排序。
        private static List<string> LevelFilterValues() {
            int sig = FilterOptionsSignature(true);
            if (sig == _levelOptSig && _levelOpts.Count > 0) return _levelOpts;
            List<string> vals = new List<string>();
            try {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null || string.IsNullOrEmpty(p.Level)) continue;
                    if (seen.Add(p.Level)) vals.Add(p.Level);
                }
                vals.Sort(delegate(string a, string c) {
                    return string.Compare(LevelDisplayNameForScene(a), LevelDisplayNameForScene(c), StringComparison.OrdinalIgnoreCase);
                });
                vals.Insert(0, "");
                _levelOptVals = vals.ToArray();
                string[] labels = new string[vals.Count];
                for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : LevelDisplayNameForScene(vals[i]);
                _levelOptLabels = labels;
                _levelOptSig = sig;
                _levelOpts = vals;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/关卡筛选项", __ex);
            }
            return vals;
        }

        private static void RenderModeFilterCombo() {
            try {
                List<string> vals = ModeFilterValues();
                if (_modeOptVals == null || _modeOptVals.Length != vals.Count) { _modeOptVals = vals.ToArray(); _modeOptLabels = null; }
                if (_modeOptLabels == null || _modeOptLabels.Length != vals.Count) {
                    string[] labels = new string[vals.Count];
                    for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : ModeText(vals[i]);
                    _modeOptLabels = labels;
                }
                RenderFilterCombo("模式", _modeOptVals, _modeOptLabels,
                    delegate { return _modeFilter; }, delegate(string v) { _modeFilter = v; },
                    ref _modeFilterOpen, "", W(110));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/渲染模式筛选", __ex); }
        }

        // 模式筛选选项：第一项为空（全部），其余为当前预设里出现过的模式目录名，按本地化模式名排序。
        private static List<string> ModeFilterValues() {
            int sig = FilterOptionsSignature(false);
            if (sig == _modeOptSig && _modeOpts.Count > 0) return _modeOpts;
            List<string> vals = new List<string>();
            try {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _presets.Count; i++) {
                    PresetInfo p = _presets[i];
                    if (p == null || string.IsNullOrEmpty(p.Mode)) continue;
                    if (seen.Add(p.Mode)) vals.Add(p.Mode);
                }
                vals.Sort(delegate(string a, string c) {
                    return string.Compare(ModeText(a), ModeText(c), StringComparison.OrdinalIgnoreCase);
                });
                vals.Insert(0, "");
                _modeOptVals = vals.ToArray();
                string[] labels = new string[vals.Count];
                for (int i = 0; i < vals.Count; i++) labels[i] = vals[i].Length == 0 ? SRApi.T("全部", "All") : ModeText(vals[i]);
                _modeOptLabels = labels;
                _modeOptSig = sig;
                _modeOpts = vals;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/模式筛选项", __ex);
            }
            return vals;
        }

        private static void RenderTypeFilterCombo() {
            try {
                string[] vals = new string[] { "", "preset", "local" };
                string[] labels = new string[] {
                    SRApi.T("全部", "All"),
                    SRApi.T("预设", "Presets"),
                    SRApi.T("本地", "Local")
                };
                RenderFilterCombo("类型", vals, labels,
                    delegate { return _typeFilter; }, delegate(string v) { _typeFilter = v; },
                    ref _typeFilterOpen, "preset", W(110));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/渲染类型筛选", __ex); }
        }

        // 加载模式下拉：默认 + 游戏四模式（派对 / 创意 / 挑战 / 自由）。运行期状态，不落配置。
        // 值为 "" 或 GameState.GameMode 的 ToString；显示名复用 ModeText（游戏本地化 / 中文回退）。
        private static void RenderLoadModeCombo() {
            try {
                string[] vals = new string[] { "", "PARTY", "CREATIVE", "CHALLENGE", "FREEPLAY" };
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) {
                    if (string.Equals(vals[i], _loadMode, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
                // 运行期状态被外部改坏时回退到默认，避免下拉框显示空白。
                if (cur == 0 && !string.IsNullOrEmpty(_loadMode)) _loadMode = "";
                string[] labels = new string[vals.Length];
                labels[0] = SRApi.T("默认", "Default");
                for (int i = 1; i < vals.Length; i++) labels[i] = ModeText(vals[i]);
                bool open = _loadModeOpen;
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, W(110));
                _loadModeOpen = open;
                if (sel >= 0 && sel < vals.Length) _loadMode = vals[sel];
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染加载模式", __ex);
            }
        }

        // 排序下拉：只有 时间 / 名称 两项。
        private static void RenderSortCombo() {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry(Section, "Sort Mode");
                string[] opts = new[] { SRApi.T("时间", "Time"), SRApi.T("名称", "Name") };
                int[] vals = new int[] { 0, 1 };
                int cur = Mathf.Clamp(SortMode, 0, opts.Length - 1);
                bool open;
                if (e == null || !SRApi.Ctl.EditOpen.TryGetValue(e, out open)) open = false;
                int sel = SRApi.Ctl.ComboBox(e, opts[cur], vals, opts, ref open, W(110));
                if (sel >= 0) {
                    if (e != null) SRApi.Ctl.SetValue(e, vals[sel]);
                    if (e != null) SRApi.Ctl.EditOpen[e] = false;
                } else if (e != null) {
                    SRApi.Ctl.EditOpen[e] = open;
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/渲染排序下拉", __ex);
            }
        }
    }
}
