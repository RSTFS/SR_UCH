// Online 的 partial 分片（只做代码组织，无逻辑改动）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    public partial class Online {
        private static string Tr(string key, string fallback) {
            try {
                string s = I2.Loc.LocalizationManager.GetTranslation(key);
                if (!string.IsNullOrEmpty(s)) return s;
            } catch { }
            return fallback;
        }

        // 界面语言为英文系时不使用游戏术语，改用自己的英文；判据必须走 SRApi.Ctl.LangEn（兼容外部语言包）。
        private static bool En { get { return SRApi.Ctl.LangEn; } }

        // 语言包优先：SRApi.T 命中外部语言包即为译文，未收录时按英文系 / 中文回落。
        private static string G(string i2Key, string zh, string en) {
            string own = SRApi.T(zh, en);
            if (En) return own;                 //英文系：我们自己的英文（或语言包译文）
            return Tr(i2Key, own);              //中文系：优先游戏自己的术语，取不到再用我们自己的中文
        }

        // 区域：取游戏自己的本地化短名，压成语言无关的代码（AP/EU/US/原始名），显示时才翻译。
        // 两级缓存：按区域对象的类型缓存成员信息；按房间 id 缓存解析结果（重新搜索时清空）。
        private sealed class RegionMember { public PropertyInfo Loc; public FieldInfo Short, Name, Id; }
        private static readonly Dictionary<Type, RegionMember> _regionMembers = new Dictionary<Type, RegionMember>();
        private static readonly Dictionary<string, string> _regionCodeCache = new Dictionary<string, string>();

        private static RegionMember RegionMembers(Type t) {
            RegionMember m;
            if (_regionMembers.TryGetValue(t, out m)) return m;
            m = new RegionMember();
            // 属性没有 SRApi.Ref 的按名取法，保留 raw 反射；字段走 SRApi.Ref.Field（失效会进自检告警）。
            try { m.Loc = t.GetProperty("LocalizedShortName"); } catch { }
            m.Short = SRApi.Ref.Field(t, "shortName", "联机列表");
            m.Name = SRApi.Ref.Field(t, "name", "联机列表");
            m.Id = SRApi.Ref.Field(t, "id", "联机列表");
            _regionMembers[t] = m;
            return m;
        }

        private static string RegionCode(Matchmaker.LobbyListInfo li) {
            if (li == null) return "";
            string id = li.sLobbyID;
            string cached;
            if (!string.IsNullOrEmpty(id) && _regionCodeCache.TryGetValue(id, out cached)) return cached;
            string code = RegionCodeUncached(li);
            if (!string.IsNullOrEmpty(id)) _regionCodeCache[id] = code;
            return code;
        }

        private static string RegionCodeUncached(Matchmaker.LobbyListInfo li) {
            // UnityServerRegion 可能为 null（房主没写 unityRelayRegion 时游戏自己也会空引用）：
            // 返回独立代码 NoneCode，避免区域筛选在 AP/EU/US 档位上静默滤掉这些房间。
            if (li != null && li.UnityServerRegion == null) return NoneCode;
            try { return RegionCodeOf(li != null ? li.UnityServerRegion : null); } catch { return NoneCode; }
        }

        // "拿不到区域对象"的专用代码，取值不可能与任何真实区域相等。
        private const string NoneCode = "（无区域）";

        // 把区域对象压成语言无关的代码（AP/EU/US/原始名/""）；RegionVals 也复用这一套映射。
        private static string RegionCodeOf(object r) {
            string loc = "", raw = "";
            try {
                if (r != null) {
                    RegionMember m = RegionMembers(r.GetType());
                    if (m.Loc != null) loc = m.Loc.GetValue(r, null) as string;
                    if (m.Short != null) raw = m.Short.GetValue(r) as string;
                    if (string.IsNullOrEmpty(raw) && m.Name != null) raw = m.Name.GetValue(r) as string;
                    if (string.IsNullOrEmpty(raw) && m.Id != null) raw = m.Id.GetValue(r) as string;
                }
            } catch { }
            string s = ((loc ?? "") + " " + (raw ?? "")).ToLowerInvariant();
            // 按词 / 后缀判断：region_shortname_ap、ap-southeast、asia 均归亚太。
            if (s.Contains("europe") || s.Contains("_eu") || s.Contains("-eu") || s.Contains(" eu")) return "EU";
            if (s.Contains("asia") || s.Contains("_ap") || s.Contains("-ap") || s.Contains("_as") || s.Contains(" ap")) return "AP";
            if (s.Contains("america") || s.Contains("_us") || s.Contains("-us") || s.Contains("_na") || s.Contains(" us")) return "US";
            if (!string.IsNullOrEmpty(raw)) return raw;   // 认不出来时用原始名
            if (!string.IsNullOrEmpty(loc) && loc.IndexOf("Network/") < 0) return loc;
            return "";
        }

        // 术语表查不到时 I2 会原样返回 key，这里过滤掉。
        private static string ZhTerm(string key, string zh) {
            try {
                string s = I2.Loc.LocalizationManager.GetTranslation(key);
                if (!string.IsNullOrEmpty(s) && s.IndexOf("Network/") < 0 && s.IndexOf("region_") < 0 && s.IndexOf('/') < 0) return s;
            } catch { }
            return zh;
        }

        private static string DispRegion(string code) {
            // 先过 SRApi.T：语言包可覆盖"亚太/欧盟/美国"，也可覆盖 "AP"/"EU"/"US" 这些英文原名。
            if (code == "AP") { string ap = SRApi.T("亚太", "AP"); return En ? ap : ZhTerm("Network/region_shortname_ap", ap); }
            if (code == "EU") { string eu = SRApi.T("欧盟", "EU"); return En ? eu : ZhTerm("Network/region_shortname_eu", eu); }
            if (code == "US") { string us = SRApi.T("美国", "US"); return En ? us : ZhTerm("Network/region_shortname_us", us); }
            // 区域对象为 null（游戏没给该字段 / id 对不上）：说明是取不到，而非某个区域。
            if (code == NoneCode) return SRApi.T("未知(无区域数据)", "unknown (no region)");
            if (string.IsNullOrEmpty(code)) return SRApi.T("未知", "unknown");
            return code;
        }

        private static string RegionShort(Matchmaker.LobbyListInfo li) { return DispRegion(RegionCode(li)); }

        // 房间满 4 人不可加入（游戏自己的列表也是 Players"/4"）。
        private static bool IsFull(Matchmaker.LobbyListInfo li) { return li.Players >= 4; }

        private static bool IsJoinable(Matchmaker.LobbyListInfo li) {
            try {
                if (li.error || !li.InfoReceived) return false;
                if (li.matchProgress != 0) return false; // 进度 != 0 = 已开局，进去会卡加载
                if (li.isAFK) return false;
                if (IsFull(li)) return false;
                return true;
            } catch { return false; }
        }

        // 按钮 / 提示文字：说明不能加入的原因。
        private static string JoinText(Matchmaker.LobbyListInfo li) {
            // "房间已空"必须在 IsJoinable 之前判定：IsJoinable 只看 Players >= 4，0 人会被算作可加入。
            if (li.Players == 0) return SRApi.T("房间已空", "empty");
            if (IsJoinable(li)) return SRApi.T("加入", "Join");
            if (li.error || !li.InfoReceived) return SRApi.T("信息不全", "no info");
            if (li.matchProgress != 0) return SRApi.T("对局中", "in match");
            if (li.isAFK) return SRApi.T("房主AFK", "host AFK");
            if (IsFull(li)) return SRApi.T("人满", "full");
            return SRApi.T("不可加入", "closed");
        }

        // 房主是否已失联（僵尸房间）：房主每 10 秒上报一次心跳，断 10 秒即视为房间已死。
        // 仅用于显示标记，不参与 IsJoinable 与筛选。
        private static bool IsStale(Matchmaker.LobbyListInfo li) {
            try {
                if (li == null || li.lastHearbeatTime == 0u) return false;   // 没上报过 / 字段缺时不猜
                uint now = 0u;
                var cl = Matchmaker.CurrentMatchmakingLobby;
                if (cl != null) now = cl.GetServerTime();
                if (now == 0u) return false;                                  // 拿不到服务器时间时不猜
                return now > li.lastHearbeatTime + 10u;
            } catch { return false; }
        }

        // 对局进度：游戏自己的写法（0 -> 大厅中 / 房主AFK；否则 100 - matchProgress %）。
        private static string ProgressText(Matchmaker.LobbyListInfo li) {
            try {
                string txt;
                if (li.matchProgress == 0) {
                    txt = li.isAFK ? G("Network/MatchProgressAFK", "房主AFK", "Host AFK")
                                   : G("Network/MatchProgressLobby", "大厅中", "In lobby");
                } else {
                    txt = (100 - li.matchProgress) + "%";
                }
                if (IsStale(li)) txt = " " + txt;   // 心跳已断：仍显示"大厅中"但点不进去
                return txt;
            } catch { return "?"; }
        }

        // 游戏质量：按 LobbyHealthNum 对比 Good/OkMatchScore；技能匹配质量系数默认不显示，
        // Shift+单击该列表头打开后，在标记（✓）后用「 · 0.87」形式追加 CalculatedSkillMatchQuality（0~1）。
        private static string QualityText(Matchmaker.LobbyListInfo li) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                string s = "";
                if (gs != null) {
                    if (li.LobbyHealthNum >= gs.GoodMatchScore) s = "✓✓";
                    else if (li.LobbyHealthNum >= gs.OkMatchScore) s = "✓";
                    if (li.CalculatedSkillMatchQuality >= gs.SkillMatchQualityThreshold) s += "✓";
                }
                if (_showQualityCoeff == null || !_showQualityCoeff.Value) return s.Length > 0 ? s : "-";
                string num = li.CalculatedSkillMatchQuality.ToString("F2", CultureInfo.InvariantCulture);
                return (s.Length > 0 ? s + " · " : "") + num;
            } catch { return "?"; }
        }

        // 游戏模式：默认规则集用游戏本地化的模式名（中文时）；自定义规则集用规则集名。
        private static string ModeText(Matchmaker.LobbyListInfo li) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                bool custom = gs != null && gs.DefaultRuleset != null && li.rulePreset != gs.DefaultRuleset.Name;
                if (custom) return En ? li.rulePreset : Tr("RuleBook/Presets/" + li.rulePreset, li.rulePreset);
                if (!En) {
                    string s = GameState.GetLocalizedGameModeName(li.gameMode);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                return DispName("Filter Mode", li.gameMode.ToString());
            } catch { return li.gameMode.ToString(); }
        }

        private static string TagText(Matchmaker.LobbyListInfo li) {
            if (!En) {
                try {
                    string s = GameSettings.ConvertLobbyTag(li.lobbyTag);
                    if (!string.IsNullOrEmpty(s)) return s;
                } catch { }
            }
            return DispName("Filter Tag", li.lobbyTag.ToString());
        }

        // 限制：制胜积分（pointLimit/50）与长度限制（轮数 / 时间 / 无限制）。
        private static string LimitText(Matchmaker.LobbyListInfo li) {
            try {
                bool noLimitMode = li.gameMode == GameState.GameMode.FREEPLAY || li.gameMode == GameState.GameMode.CHALLENGE;
                string win = noLimitMode ? "--" : (li.pointLimit / 50).ToString();
                string len;
                if (noLimitMode) len = G("RuleBook/NoLimit", "无限制", "no limit");
                else if (li.limitType == GameLimitType.TIME) len = (li.limitAmount / 60) + G("RuleBook/minutesAbbreviation", "分", " min");
                else if (li.limitType == GameLimitType.ROUNDS) len = li.limitAmount + G("RuleBook/Rounds", "轮", " rd");
                else len = G("RuleBook/NoLimit", "无限制", "no limit");
                return win + " / " + len;
            } catch { return "?"; }
        }

        private static string FlagsText(Matchmaker.LobbyListInfo li) {
            string s = li.usingMods ? SRApi.T("修改器", "mod") : "";
            if (li.isAFK) s += (s.Length > 0 ? "+" : "") + "AFK";
            return s;
        }

        private static string Val(ConfigEntry<string> e) { return e != null ? (e.Value ?? "") : ""; }

        private static bool PassFilter(Matchmaker.LobbyListInfo li) {
            try {
                string v;
                v = Val(_fRegion);
                if (v.Length > 0 && RegionCode(li) != v) return false;
                v = Val(_fMode);
                if (v.Length > 0) {
                string __gm = li.gameMode.ToString();
                if (v == "OTHER") { if (__gm == "FREEPLAY" || __gm == "CREATIVE" || __gm == "PARTY" || __gm == "CHALLENGE") return false; } // 其它 = 四种已知模式之外
                else if (__gm != v) return false;
            }
                v = Val(_fTag);
                if (v.Length > 0 && li.lobbyTag.ToString() != v) return false;
                v = Val(_fState);
                if (v == "join") { if (!IsJoinable(li)) return false; }        //可加入：游戏自己的"能不能进"判据
                else if (v == "full") { if (!IsFull(li)) return false; }        //人满
                else if (v == "playing") { if (li.matchProgress == 0) return false; } //对局中：进度 != 0
                return true;
            } catch { return true; }
        }

        private static int CountShown() {
            int n = 0;
            for (int i = 0; i < _lobbies.Count; i++) {
                Matchmaker.LobbyListInfo li = _lobbies[i];
                if (li == null || !PassFilter(li)) continue;
                if (_hidden.Contains(li.sLobbyID)) continue;
                n++;
            }
            return n;
        }

        // 排序：表头选定的排序列优先；否则可加入的排最前，其内部按房间健康分降序，其余按进度。
        // 最后用 sLobbyID 兜底（List.Sort 不稳定，键相同会每帧互换导致表格闪）。
        private static void SortLobbies() {
            try {
                _lobbies.Sort((a, b) => {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    if (_sortCol >= 0) {
                        int r = CompareCol(_sortCol, a, b);
                        if (r != 0) return _sortDesc ? -r : r;
                        return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
                    }
                    bool ja = IsJoinable(a), jb = IsJoinable(b);
                    if (ja != jb) return ja ? -1 : 1;
                    // 只在两边都可加入时按游戏自己的 CombinedHealthSkill 降序（后端已算好）。
                    if (ja) {
                        int h = b.CombinedHealthSkill.CompareTo(a.CombinedHealthSkill);   //降序
                        if (h != 0) return h;
                    }
                    int c = a.matchProgress.CompareTo(b.matchProgress);
                    if (c != 0) return c;
                    return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
                });
            } catch (Exception __ex) { SRApi.Guard.Log("房间排序", __ex); }
        }

        // 按某一列比较两个房间：数值列用真实数值（文本比较下 "10" < "2"），其余列用显示文本。
        // 列索引与 Online.cs 的 ColCount/Titles 对齐（v5：删除技能系数列后进度列前移一位）。
        private static int CompareCol(int ci, Matchmaker.LobbyListInfo a, Matchmaker.LobbyListInfo b) {
            switch (ci) {
                case 1: return a.Players.CompareTo(b.Players);
                case 2: return a.LobbyHealthNum.CompareTo(b.LobbyHealthNum);
                case 3: return a.matchProgress.CompareTo(b.matchProgress);
                case CombinedCol: return a.CombinedHealthSkill.CompareTo(b.CombinedHealthSkill);
                default: {
                    string[] ca = CellTexts(a), cb = CellTexts(b);
                    string ta = ci < ca.Length ? ca[ci] : null, tb = ci < cb.Length ? cb[ci] : null;
                    return string.Compare(ta ?? "", tb ?? "", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // 已隐藏房间数：只数当前列表里仍存在的（_hidden 跨刷新累积，直接用其 Count 会偏大）。
        private static int CountHidden() {
            if (_hidden.Count == 0) return 0;   // 未隐藏过任何房间时不必每帧遍历列表
            int n = 0;
            try {
                for (int i = 0; i < _lobbies.Count; i++) {
                    Matchmaker.LobbyListInfo li = _lobbies[i];
                    if (li != null && _hidden.Contains(li.sLobbyID)) n++;
                }
            } catch { }
            return n;
        }

        // _hidden 只增不减，长期挂机会累积；在搜索结束那一帧做一次交集裁剪，只保留当前列表里仍存在的 id。
        // 用 IntersectWith 原地求交集（net472 的 HashSet 没有 RemoveWhere）。
        private static void PruneHidden() {
            if (_hidden.Count == 0) return;
            try {
                HashSet<string> alive = new HashSet<string>();
                for (int i = 0; i < _lobbies.Count; i++) {
                    Matchmaker.LobbyListInfo li = _lobbies[i];
                    if (li != null && !string.IsNullOrEmpty(li.sLobbyID)) alive.Add(li.sLobbyID);
                }
                _hidden.IntersectWith(alive);
            } catch (Exception __ex) { SRApi.Guard.Log("裁剪已隐藏房间集合", __ex); }
        }

        private static void UpdateLobbyMsg() {
            _lobbyMsg = string.Format(SRApi.T("共 {0} 个房间，筛选后 {1} 个", "{0} lobbies, {1} after filters"), _lobbies.Count, CountShown());
        }

        // 页面：联机
    }
}
