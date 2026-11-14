// Online 分片：联机列表的列渲染、筛选、排序与心跳/区域判定。
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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return fallback;
        }

        private static bool En { get { return SRApi.Ctl.LangEn; } }

        private static string G(string i2Key, string zh, string en) {
            string own = SRApi.T(zh, en);
            if (En) return own;                 // 英文系：我们自己的英文（或语言包译文）
            return Tr(i2Key, own);              // 中文系：优先游戏自己的术语，取不到再用我们自己的中文
        }

        private sealed class RegionMember { public PropertyInfo Loc; public FieldInfo Short, Name, Id; }
        private static readonly Dictionary<Type, RegionMember> _regionMembers = new Dictionary<Type, RegionMember>();
        private static readonly Dictionary<string, string> _regionCodeCache = new Dictionary<string, string>();
        private const int RegionCodeCacheMax = 512;

        // 区域缓存两级：类型级成员信息 + 房间 id 级解析结果；重新搜索时清掉后者（区域对象可能换一批）。
        internal static void ClearRegionCodeCache() {
            try { _regionCodeCache.Clear(); } catch (Exception __ex) { SRApi.Guard.Log("联机/清区域缓存", __ex); }
        }

        private static RegionMember RegionMembers(Type t) {
            RegionMember m;
            if (_regionMembers.TryGetValue(t, out m)) return m;
            m = new RegionMember();
            try { m.Loc = t.GetProperty("LocalizedShortName"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            try {
                string tn = t != null ? t.Name : "Region";
                SRApi.Ref.Override(tn + ".shortName", "联机列表：区域短名");
                SRApi.Ref.Override(tn + ".name", "联机列表：区域名");
                SRApi.Ref.Override(tn + ".id", "联机列表：区域编号");
                SRApi.Ref.Override(tn + ".LocalizedShortName", "联机列表：区域本地化短名（属性）");
            } catch (Exception __ro) { SRApi.Guard.Log("联机/登记区域字段", __ro); }
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
            if (!string.IsNullOrEmpty(id)) {
                if (_regionCodeCache.Count >= RegionCodeCacheMax) _regionCodeCache.Clear();   // 满了整体清：解析成本极低
                _regionCodeCache[id] = code;
            }
            return code;
        }

        private static string RegionCodeUncached(Matchmaker.LobbyListInfo li) {
            // UnityServerRegion 可能为 null（房主没写 unityRelayRegion）：返回独立代码，避免被区域筛选静默滤掉。
            if (li != null && li.UnityServerRegion == null) return NoneCode;
            try { return RegionCodeOf(li != null ? li.UnityServerRegion : null); } catch { return NoneCode; }
        }

        // “拿不到区域对象”的专用代码，取值不可能与任何真实区域相等。
        private const string NoneCode = "（无区域）";

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
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            string s = ((loc ?? "") + " " + (raw ?? "")).ToLowerInvariant();
            if (s.Contains("europe") || s.Contains("_eu") || s.Contains("-eu") || s.Contains(" eu")) return "EU";
            if (s.Contains("asia") || s.Contains("_ap") || s.Contains("-ap") || s.Contains("_as") || s.Contains(" ap")) return "AP";
            if (s.Contains("america") || s.Contains("_us") || s.Contains("-us") || s.Contains("_na") || s.Contains(" us")) return "US";
            if (!string.IsNullOrEmpty(raw)) return raw;   // 认不出来时用原始名
            if (!string.IsNullOrEmpty(loc) && loc.IndexOf("Network/") < 0) return loc;
            return "";
        }

        // 区域归一化：把代码、“亚太/欧盟/美国”、region_shortname_xx、英文名统一归到 AP/EU/US。
        private static string NormRegion(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            string x = s.ToLowerInvariant();
            if (x.Contains("europe") || x.Contains("_eu") || x.Contains("-eu") || x.Contains(" eu") || x.Contains("欧盟") || x.Contains("欧洲")) return "EU";
            if (x.Contains("asia") || x.Contains("_ap") || x.Contains("-ap") || x.Contains(" ap") || x.Contains("亚太") || x.Contains("亚洲")) return "AP";
            if (x.Contains("america") || x.Contains("_us") || x.Contains("-us") || x.Contains("_na") || x.Contains(" us") || x.Contains("美国") || x.Contains("美洲") || x.Contains("北美")) return "US";
            return s;
        }

        private static string DispRegion(string code) {
            if (code == NoneCode || string.IsNullOrEmpty(code)) return "-";
            string n = NormRegion(code);
            if (n == "AP") return SRApi.T("亚太", "Asia Pacific");
            if (n == "EU") return SRApi.T("欧盟", "Europe");
            if (n == "US") return SRApi.T("美国", "America");
            return code;   // 认不出来的原始短名原样显示
        }

        private static string RegionShort(Matchmaker.LobbyListInfo li) {
            try {
                object r = li != null ? li.UnityServerRegion : null;
                if (r != null) {
                    RegionMember m = RegionMembers(r.GetType());
                    string loc = m.Loc != null ? (m.Loc.GetValue(r, null) as string) : null;
                    if (!string.IsNullOrEmpty(loc) && loc.IndexOf('/') < 0 && loc.IndexOf("region_", StringComparison.OrdinalIgnoreCase) < 0
                        && loc.IndexOf("Network/", StringComparison.OrdinalIgnoreCase) < 0)
                        return loc;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return DispRegion(RegionCode(li));
        }

        // 房间满 4 人不可加入。
        private static bool IsFull(Matchmaker.LobbyListInfo li) { return li.Players >= 4; }

        // 这一行是不是“我们已经在的房间”：加入时记下的房号与行一致，且当前确实在会话里。
        private static bool IsSelfLobby(Matchmaker.LobbyListInfo li) {
            try {
                if (li == null) return false;
                string joined = Online.JoinedLobbyId;
                if (string.IsNullOrEmpty(joined)) return false;
                string key = !string.IsNullOrEmpty(li.sLobbyID) ? li.sLobbyID
                    : (li.UnityMatchID != 0UL ? li.UnityMatchID.ToString(CultureInfo.InvariantCulture) : (li.LobbyOwner ?? ""));
                return key == joined;
            } catch { return false; }
        }

        private static bool IsJoinable(Matchmaker.LobbyListInfo li) {
            try {
                if (li.error || !li.InfoReceived) return false;
                if (IsSelfLobby(li)) return false;
                if (li.matchProgress != 0) return false; // 进度 != 0 = 已开局，进去会卡加载
                if (li.isAFK) return false;
                if (li.Players <= 0) return false;   // 0 人房不可加入
                if (IsFull(li)) return false;
                return true;
            } catch { return false; }
        }

        // 提示文字按优先级判定：已加入 > 房间已空 > 已失联 > 可加入 > 信息不全 > 对局中 > 房主 AFK > 人满。
        private static string JoinText(Matchmaker.LobbyListInfo li) {
            if (IsSelfLobby(li)) return SRApi.T("已进入", "joined");
            if (li.Players == 0) return SRApi.T("房间已空", "empty");
            if (IsStale(li) && !IsJoinable(li)) return SRApi.T("已失联", "No heartbeat");
            if (IsJoinable(li)) return SRApi.T("加入", "Join");
            if (li.error || !li.InfoReceived) return SRApi.T("信息不全", "no info");
            if (li.matchProgress != 0) return SRApi.T("对局中", "in match");
            if (li.isAFK) return SRApi.T("房主AFK", "host AFK");
            if (IsFull(li)) return SRApi.T("人满", "full");
            return SRApi.T("不可加入", "closed");
        }

        // 房主失联（僵尸房间）：断心跳超过 30 秒即视为房间已死，仅用于显示。
        private const float StaleSeconds = 30f;   // 心跳停更 30 秒才标记失联（10 秒太容易误判）
        private static readonly Dictionary<string, float> _hbSeenAt = new Dictionary<string, float>();
        private static readonly Dictionary<string, uint> _hbValue = new Dictionary<string, uint>();
        // 心跳按本地观察判定：值变了刷新，持续超过 StaleSeconds 才算失联，不与服务器时钟相减。

        private static bool IsStale(Matchmaker.LobbyListInfo li) {
            try {
                if (li == null || li.lastHearbeatTime == 0u) return false;   // 没上报过 / 字段缺时不猜
                string id = li.sLobbyID ?? "";
                if (id.Length == 0) return false;
                float now = Time.realtimeSinceStartup;
                uint seen;
                if (!_hbValue.TryGetValue(id, out seen) || seen != li.lastHearbeatTime) {
                    _hbValue[id] = li.lastHearbeatTime;
                    _hbSeenAt[id] = now;
                    if (_hbValue.Count > 512) { _hbValue.Clear(); _hbSeenAt.Clear(); _hbValue[id] = li.lastHearbeatTime; _hbSeenAt[id] = now; }
                    return false;                                            // 心跳刚更新
                }
                float first;
                if (!_hbSeenAt.TryGetValue(id, out first)) { _hbSeenAt[id] = now; return false; }
                return now - first > StaleSeconds;
            } catch { return false; }
        }

        // 对局进度按游戏自己的写法：0 = 大厅中 / 房主 AFK，否则 100 - matchProgress %。
        private static string ProgressText(Matchmaker.LobbyListInfo li) {
            try {
                string txt;
                if (li.matchProgress == 0) {
                    txt = li.isAFK ? G("Network/MatchProgressAFK", "房主AFK", "Host AFK")
                                   : G("Network/MatchProgressLobby", "大厅中", "In lobby");
                } else {
                    txt = (100 - li.matchProgress) + "%";
                }
                if (IsStale(li)) txt = " " + txt;   // 心跳已断：仍显示“大厅中”但点不进去
                return txt;
            } catch { return "?"; }
        }

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
                } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
            return DispName("Filter Tag", li.lobbyTag.ToString());
        }

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
            return li.usingMods ? SRApi.T("修改器", "mod") : "";
        }

        private static string Val(ConfigEntry<string> e) { return e != null ? (e.Value ?? "") : ""; }
        // 限制列：制胜积分（pointLimit/50）与长度限制（轮数 / 时间 / 无限制）。

        private static bool PassFilter(Matchmaker.LobbyListInfo li) {
            try {
                string v;
                v = Val(_fRegion);
                if (v.Length > 0) {
                    string want = NormRegion(v);
                    string have = NormRegion(RegionCode(li));
                    if (want != have) return false;
                }
                v = Val(_fMode);
                if (v.Length > 0) {
                string __gm = li.gameMode.ToString();
                if (v == "OTHER") { if (__gm == "FREEPLAY" || __gm == "CREATIVE" || __gm == "PARTY" || __gm == "CHALLENGE") return false; } // 其它 = 四种已知模式之外
                else if (__gm != v) return false;
            }
                v = Val(_fTag);
                if (v.Length > 0 && li.lobbyTag.ToString() != v) return false;
                v = Val(_fState);
                if (v == "join") { if (!IsJoinable(li)) return false; }        // 可加入：游戏自己的“能不能进”判据
                else if (v == "full") { if (!IsFull(li)) return false; }        // 人满
                else if (v == "playing") { if (li.matchProgress == 0) return false; } // 对局中：进度 != 0
                return true;
            } catch { return true; }
        }

        private static int CountShown() {
            int n = 0;
            for (int i = 0; i < _lobbies.Count; i++) {
                Matchmaker.LobbyListInfo li = _lobbies[i];
                if (li == null || !PassFilter(li)) continue;
                n++;
            }
            return n;
        }

        // 排序：表头选定的排序列优先；否则可加入的排最前，其内部按健康分降序，其余按进度。
        // 最后用 sLobbyID 兜底（List.Sort 不稳定，键相同会每帧互换导致表格闪）。
        // 筛选谓词：区域/模式/标签/状态 + 隐藏列表（供核心 TableSource 使用）。
        private static bool PassLobbyFilter(Matchmaker.LobbyListInfo li) {
            try {
                if (li == null) return false;
                return PassFilter(li);
            } catch { return false; }
        }

        // 排序比较器：表头排序列优先；否则可加入的排最前，其内部按健康分降序，其余按进度；sLobbyID 兜底。
        private static int CompareLobbies(Matchmaker.LobbyListInfo a, Matchmaker.LobbyListInfo b) {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            bool sa = IsSelfLobby(a), sb = IsSelfLobby(b);
            if (sa != sb) return sa ? -1 : 1;
            if (_sortCol >= 0) {
                int r = CompareCol(_sortCol, a, b);
                if (r != 0) return _sortDesc ? -r : r;
                return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
            }
            bool ja = IsJoinable(a), jb = IsJoinable(b);
            if (ja != jb) return ja ? -1 : 1;
            if (ja) {
                int h = b.CombinedHealthSkill.CompareTo(a.CombinedHealthSkill);
                if (h != 0) return h;
            }
            int c = a.matchProgress.CompareTo(b.matchProgress);
            if (c != 0) return c;
            return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
        }

        // 按某一列比较两个房间：数值列用真实数值（文本比较下 "10" < "2"），其余列用显示文本。
        // 列索引与 Online.cs 的 ColCount/Titles 对齐。
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

        private static void UpdateLobbyMsg() {
            if (_lobbySearching) {
                _lobbyMsg = string.Format(SRApi.T("搜索中… 已找到 {0} 个（筛选后 {1} 个）", "Searching... {0} found ({1} after filters)"),
                    _lobbies.Count, CountShown());
                return;
            }
            _lobbyMsg = string.Format(SRApi.T("共 {0} 个房间，筛选后 {1} 个", "{0} lobbies, {1} after filters"), _lobbies.Count, CountShown());
        }

        // 页面：联机。
    }
}
