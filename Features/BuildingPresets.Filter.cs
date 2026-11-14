// 布局预设 · 筛选与排序：可见行集合、签名缓存、各筛选下拉。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {
        private static List<PresetInfo> VisiblePresets() {
            // 签名驱动：仅数据 / 筛选 / 排序 / 语言 / 显示模式变化时重算。
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

        // 显式枚举所有影响可见行的输入（漏掉只会让列表不刷新）；签名按帧缓存。
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

        // 仅筛选条件的签名：变化时复位滚动位置与二次确认态。
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

        // 下拉选项签名：只依赖预设集合的 Level/Mode 与语言。
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

        private static void SetFilterValue(ConfigEntry<string> e, string v) {
            try { if (e != null) { e.Value = v ?? ""; SRApi.Ctl.MarkConfigDirty(e.ConfigFile); } }
            catch (Exception __fv) { SRApi.Guard.Log("布局预设/筛选落盘", __fv); }
        }

        private static bool PassPresetFilter(PresetInfo p) {
            try {
                return PresetFilterLogic.Pass(p.Name, p.Level, p.Mode, p.IsLocal,
                    _typeFilter, _levelFilter, _modeFilter, _search);
            } catch { return false; }
        }

        private static bool ContainsText(string text, string query) {
            try { return text != null && query != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
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
                SRApi.Ctl.FilterCombo("关卡", _levelOptVals, _levelOptLabels,
                    delegate { return _levelFilter; }, delegate(string v) { SetFilterValue(_filterLevelEntry, v); _levelFilter = v; },
                    ref _levelFilterOpen, "", W(110));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/渲染关卡筛选", __ex); }
        }

        // 关卡筛选项：首项「全部」，其余为出现过的关卡场景名，按本地化名排序。
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
                SRApi.Ctl.FilterCombo("模式", _modeOptVals, _modeOptLabels,
                    delegate { return _modeFilter; }, delegate(string v) { SetFilterValue(_filterModeEntry, v); _modeFilter = v; },
                    ref _modeFilterOpen, "", W(110));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/渲染模式筛选", __ex); }
        }

        // 模式筛选项：首项「全部」，其余为出现过的模式目录名，按本地化名排序。
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
                SRApi.Ctl.FilterCombo("类型", vals, labels,
                    delegate { return _typeFilter; }, delegate(string v) { SetFilterValue(_filterTypeEntry, v); _typeFilter = v; },
                    ref _typeFilterOpen, "preset", W(110));
            } catch (Exception __ex) { SRApi.Guard.Log("布局预设/渲染类型筛选", __ex); }
        }

        // 加载模式下拉：默认 + 游戏四模式；运行期状态，不落配置。
        private static void RenderLoadModeCombo() {
            try {
                string[] vals = new string[] { "", "PARTY", "CREATIVE", "CHALLENGE", "FREEPLAY" };
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) {
                    if (string.Equals(vals[i], _loadMode, StringComparison.OrdinalIgnoreCase)) { cur = i; break; }
                }
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
