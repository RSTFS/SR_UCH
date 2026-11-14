// 联机 · 筛选与显示名（区域/模式/标签/状态四个下拉 + 候选值缓存）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class Online {
        private static string DispName(string key, string v) {
            if (string.IsNullOrEmpty(v)) return SRApi.T("全部", "All");
            if (key == "Filter Region") return DispRegion(v);
            if (key == "Filter Mode") return SRApi.T(v == "OTHER" ? "其它" : v == "FREEPLAY" ? "自由" : v == "CREATIVE" ? "创意" : v == "PARTY" ? "派对" : "挑战",
                                                  v == "FREEPLAY" ? "Freeplay" : v == "CREATIVE" ? "Creative" : v == "PARTY" ? "Party" : "Challenge");
            if (key == "Filter Tag") return SRApi.T(v == "Fun" ? "好玩" : v == "Competitive" ? "竞技" : v == "Beginner" ? "新手" : "自定义关卡",
                                                 v == "Fun" ? "Fun" : v == "Competitive" ? "Competitive" : v == "Beginner" ? "Beginner" : "Custom levels");
            if (key == "Filter State") return v == "join" ? SRApi.T("可加入", "joinable") : v == "full" ? SRApi.T("人满", "full") : v == "playing" ? SRApi.T("对局中", "in match") : SRApi.T("全部", "all");
            return v; // 区域等：值本身就是显示名
        }

        // 版本戳 = 语言 id + 语言版本 hash + AVAILABLE_REGIONS 规模（该 List 可能晚于首帧填好）。
        private static int RegionLangStamp() {
            unchecked {
                int h = 17;
                string s = SRApi.CurrentLanguageId + "|" + SRApi.LangVer;
                for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
                int n = -1;
                try { if (RelayConstants.AVAILABLE_REGIONS != null) n = RelayConstants.AVAILABLE_REGIONS.Count; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                return h * 31 + n;
            }
        }

        private static string[] RegionVals() {
            int ver = RegionLangStamp();
            if (_regionValsCache != null && _regionValsVer == ver) return _regionValsCache;
            string[] built;
            try {
                System.Collections.Generic.List<string> vs = new System.Collections.Generic.List<string>();
                System.Collections.IList regs = RelayConstants.AVAILABLE_REGIONS;
                if (regs != null) {
                    for (int i = 0; i < regs.Count; i++) {
                        object r = regs[i];
                        if (r == null) continue;
                        string code = RegionCodeOf(r);
                        if (string.IsNullOrEmpty(code)) continue;
                        if (!vs.Contains(code)) vs.Add(code);   //按 AVAILABLE_REGIONS 的原始次序去重
                    }
                }
                // 补上「无区域数据」这一档（放最后）：否则区域为 null 的房间在下拉里没有选项。
                if (!vs.Contains(NoneCode)) vs.Add(NoneCode);
                built = vs.Count > 0 ? vs.ToArray() : new[] { "AP", "EU", "US", NoneCode };
            } catch { built = new[] { "AP", "EU", "US" }; }
            _regionValsCache = built;
            _regionValsVer = ver;
            return built;
        }

        private static string[] FilterOptions(string key, string[] values) {
            // 语言戳必须带语言 id + 版本号：只用 "en"/"zh" 时切到另一语言包戳不变，选项会停在旧译文。
            string stamp = SRApi.CurrentLanguageId + SRApi.LangVer + "|" + (values == null ? "-" : values.Length.ToString());
            string[] cached;
            string old;
            if (_optCache.TryGetValue(key, out cached) && _optStamp.TryGetValue(key, out old) && old == stamp) return cached;
            List<string> opts = new List<string>(1 + (values != null ? values.Length : 0));
            opts.Add(SRApi.T("全部", "All"));
            if (values != null) {
                for (int i = 0; i < values.Length; i++) opts.Add(DispName(key, values[i]));
            }
            cached = opts.ToArray();
            _optCache[key] = cached;
            _optStamp[key] = stamp;
            return cached;
        }

        private static string[] FilterVals(string key, string[] values) {
            string stamp = SRApi.CurrentLanguageId + SRApi.LangVer + "|" + (values == null ? "-" : values.Length.ToString());
            string[] cached;
            string old;
            if (_optValsCache.TryGetValue(key, out cached) && _optStamp.TryGetValue(key, out old) && old == stamp) return cached;
            List<string> vs = new List<string>(1 + (values != null ? values.Length : 0));
            vs.Add("");
            if (values != null) for (int i = 0; i < values.Length; i++) vs.Add(values[i]);
            cached = vs.ToArray();
            _optValsCache[key] = cached;
            _optStamp[key] = stamp;
            return cached;
        }

        private static float FilterLabelWidth() {
            string lk = SRApi.CurrentLanguageId + "|" + SRApi.LangVer;
            if (_filterLabelW >= 0f && _filterLabelLKey == lk) return _filterLabelW;
            float w = 0f;
            string[] ls = { SRApi.T("区域", "Region"), SRApi.T("模式", "Mode"), SRApi.T("标签", "Tag"), SRApi.T("人数", "Players"), SRApi.T("AFK", "AFK") };
            for (int i = 0; i < ls.Length; i++) w = Mathf.Max(w, SRApi.Ctl.Label.CalcSize(new GUIContent(ls[i])).x);
            _filterLabelW = Mathf.Max(SRApi.Ctl.Sc(FilterLabelW), w + SRApi.Ctl.Sc(8));
            _filterLabelLKey = lk;
            return _filterLabelW;
        }

        private static void DropFilter(string labelZh, string labelEn, string tipZh, string tipEn, string key, string[] values, float cellW) {
            try {
                // 本帧只查一次 entry，当前值与写回都复用它（不重复 FindEntry）。
                ConfigEntryBase e = SRApi.Ctl.FindEntry(SecOnline, key);
                float labelW = FilterLabelWidth();
                float comboW = SRApi.Ctl.Sc(FilterComboW);
                GUILayout.BeginHorizontal(GUILayout.Width(labelW + comboW + SRApi.Ctl.Sc(14)));
                GUILayout.Label(new GUIContent(SRApi.T(labelZh, labelEn), SRApi.T(tipZh, tipEn)), SRApi.Ctl.Label,
                    GUILayout.Width(labelW), GUILayout.Height(SRApi.Ctl.Sc(26)));
                // 选项走缓存（见 FilterOptions），不每帧新建 List；改值不带动页面滚动条。
                string[] opts = FilterOptions(key, values);
                string[] vals = FilterVals(key, values);   // 与 opts 一一对应，选中后按下标写回
                //同时只允许一个下拉框展开
                bool open = _openCombo == key;
                int dsel = SRApi.Ctl.ComboBox(e, DispName(key, FindStr(e)), vals, opts, ref open, comboW);
                if (dsel >= 0) {
                    SetStr(e, dsel == 0 ? "" : (values != null && dsel - 1 < values.Length ? values[dsel - 1] : ""));
                    _openCombo = "";
                    open = false;
                }
                if (open) {
                    if (_openCombo != key) _openCombo = key;
                } else if (_openCombo == key) {
                    _openCombo = "";
                }
                GUILayout.EndHorizontal();
            } catch (Exception __ex) { SRApi.Guard.Log("房间筛选下拉", __ex); }
        }

        private static string FindStr(string key) {
            try { return FindStr(SRApi.Ctl.FindEntry(SecOnline, key)); }
            catch { return ""; }
        }

        private static string FindStr(ConfigEntryBase e) {
            try { return e != null ? (e.BoxedValue as string ?? "") : ""; }
            catch { return ""; }
        }

        private static void SetStr(ConfigEntryBase e, string value) {
            try {
                if (e != null) {
                    SRApi.Ctl.SetValue(e, value);
                    // 筛选条件变化需立即标脏，否则房间数要等 1 秒兜底才更新。
                    _lobbiesDirty = true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("设置房间筛选", __ex); }
        }
}
}
