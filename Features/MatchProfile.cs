// 开局档案：把「游戏原生规则 + 本 mod 的全部配置」打包成一份可复用的房间设置。
//
// 为什么只房主装就够（游戏侧证据）：
//   GameRulePreset.GetRulesetXmlString() 导出整份规则 XML                :128212
//   MsgApplyRuleset { rulesetXML, premadeIdx, applyRules/Points/Blocks/Mods, temporary } :46530
//   NotifyPresetLoad 用同一个消息类型发送                                 :112118
//   服务器 RegisterHandler(ApplyRuleset, distributeServerMessage)        :38957
//   客户端 ApplyRulesetFromMessage → LoadRulesetFromXML → ApplyTemporaryRuleset :111651-111660
// 所以规则部分由游戏自己广播并落到每个客户端；没装 mod 的房客照样生效。
// mod 配置只落房主本机（ConfigFile 是插件自己的，不跨端同步，这是预期）。
//
// 套用前的三道关（本轮新增）：
//   1) 结构兼容性：把档案里的 <rules>/<mods> 属性名与当前游戏生成的 XML 对比。
//      游戏更新删改字段时，档案里多出来的名字会立刻在页面上标 ⚠（零反射、零补丁）。
//   2) 差异预览：套用按钮第一次点击只列出「这次会改哪几项」，第二次点击才真正执行。
//      聊天指令 / 剪贴板导入走直接执行，但差异仍写进日志，事后可查。
//   3) 自检握手：套用后自动重跑一次完整自检（核心 sr.audit.rescan 槽位），
//      并把结论写进状态栏。重扫失败或规则广播异常时回滚本机 mod 配置。
//      注意：自检页那个 Rescan 只重扫补丁归因表，PatchAudit/RefAudit 有幂等锁，
//      所以核心层新加的 RescanAudit() 会显式解锁并重建问题表。
//
// 已知边界（页面里也写明）：
//   ApplyRulesetFromMessage 挂在 TabletRulesScreen 上，树屋/规则书界面一定在；
//   对局中该界面是否实例化未实机确认，因此建议【开局前 / 树屋里】套用。
//   离线且 NetworkClient.active=false 时规则消息发不出去，只恢复 mod 配置。
//
// 刻意不碰：Harmony 补丁、反射登记、新 msgType、真实分数、多线程。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.profile", "MatchProfile", "1.0", Provides = new string[] {
        "sr.profile.summary", "sr.profile.apply", "sr.profile.list" })]
    public class MatchProfile : ITweak {
        private const string Sec = "开局档案";
        private const string Header = "# SR_UCH-PROFILE v1";
        private const int MaxTextChars = 200000;
        private const int MaxList = 60;
        private const int MaxDiffLines = 28;
        private const string RescanSlot = "sr.audit.rescan";

        private static ConfigEntry<bool> _enabled, _applyMods, _includeMods, _autoCheck, _rollback;
        private static ConfigEntry<string> _newName;
        private static IFeatureHost _host;
        private static string[] _items = new string[0];
        private static string _sel = "all", _status = "";
        private static bool _openList, _deleteArmed;

        // 套用前差异：不写进 cfg，关面板后清空。
        private static string[] _diffLines = new string[0];
        private static string _diffTitle = "";
        private static bool _diffBlocked, _applyArmed, _parseOk;
        private static string _pendingText = "";
        // 自检重扫（软依赖，只查槽位名，缺核心提供时零 miss、零副作用）。
        private static Func<int> _aRescan;
        private static int _bindTries;
        private static float _nextBind;

        // ================= 生命周期 =================

        // 配置行统一走"标签 + 控件"：RenderControl 只画控件、没有名称列，
        // 那样 LocKey / LocDesc 登记不会被消费，界面上也没有任何配置项名称。
        // 列宽与核心 Sc() 同坐标系；契约层未暴露 EntryNameWidth，故用固定 180 设计像素。
        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        // 本页配置行的显示名与悬浮说明：缺 LocKey 时中文界面回退英文键名，
        // 缺 LocDesc 时英文界面完全没有提示（内部条目不会回退 Bind 的中文描述）。
        private static void RegUi() {
            SRApi.LocKey(Sec, "Enabled", "启用开局档案", "Enable match profile");
            SRApi.LocDesc(Sec, "Enabled",
                "启用开局档案\n启用开局档案（保存 / 套用房间设置）。",
                "Enable match profile\nSave / apply room settings as a named profile.");
            SRApi.LocKey(Sec, "Include Mod Config", "保存时打包 mod 配置", "Include mod config");
            SRApi.LocDesc(Sec, "Include Mod Config",
                "保存时打包 mod 配置\n保存档案时一起打包本 mod 的配置项。",
                "Include mod config\nBundle this mod's config entries when saving a profile.");
            SRApi.LocKey(Sec, "Apply Mod Config", "套用时恢复 mod 配置", "Apply mod config");
            SRApi.LocDesc(Sec, "Apply Mod Config",
                "套用时恢复 mod 配置\n套用档案时恢复本 mod 的配置项（规则仍会广播）。",
                "Apply mod config\nRestore this mod's config when applying a profile; game rules are still broadcast.");
            SRApi.LocKey(Sec, "Profile Name", "档案名称", "Profile name");
            SRApi.LocDesc(Sec, "Profile Name",
                "档案名称\n保存时用的名字；留空则用时间戳。",
                "Profile name\nName used when saving; empty uses a timestamp.");
            SRApi.LocKey(Sec, "Auto Self-check", "套用后自动自检", "Auto self-check");
            SRApi.LocDesc(Sec, "Auto Self-check",
                "套用后自动自检\n套用完成后重跑一次完整自检（补丁目标 / 反射成员 / 归因表），结论写进状态栏。",
                "Auto self-check\nRerun the full self-check (patch targets / reflection / attribution) after applying and show the verdict.");
            SRApi.LocKey(Sec, "Rollback On Failure", "失败时回滚配置", "Rollback on failure");
            SRApi.LocDesc(Sec, "Rollback On Failure",
                "失败时回滚配置\n规则广播异常或自检重扫出错时，把本机 mod 配置还原到套用前。",
                "Rollback on failure\nRestore host-local mod config if rule broadcast or the self-check rescan errors out.");
        }

        public void Initialize(IFeatureHost plugin) {
            _host = plugin;
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "启用开局档案（保存 / 套用房间设置）。");
            _includeMods = plugin.Config.Bind(Sec, "Include Mod Config", true, "保存档案时一起打包本 mod 的配置项。");
            _applyMods = plugin.Config.Bind(Sec, "Apply Mod Config", true, "套用档案时恢复本 mod 的配置项（规则仍会广播）。");
            _newName = plugin.Config.Bind(Sec, "Profile Name", "", "保存时用的名字；留空则用时间戳。");
            _autoCheck = plugin.Config.Bind(Sec, "Auto Self-check", true, "套用完成后重跑一次完整自检，结论写进状态栏。");
            _rollback = plugin.Config.Bind(Sec, "Rollback On Failure", true, "规则广播异常或自检重扫出错时还原本机 mod 配置。");

            SRApi.LocSec(Sec, "开局档案", "Match Profile");

            RegUi();
            SRApi.Nav(Sec, 119);  // 通知中心(118) 之后，避开 rank 撞号
            SRApi.RegisterPage(Sec, "Profiles", "档案", "Profiles", 10, RenderPage);
            SRApi.RegisterTick("开局档案-绑定自检槽位", Tick);

            Slots.Provide("sr.profile.summary", (Func<string>)(() => Summary()));
            Slots.Provide("sr.profile.apply", (Action<string>)(n => ApplyByName(n)));
            Slots.Provide("sr.profile.list", (Func<string[]>)(() => ListProfiles()));
            _bindTries = 0;
            _nextBind = Time.unscaledTime + 0.5f;
            Refresh();
        }

        private static void Tick() {
            if (_bindTries >= 8 || Time.unscaledTime < _nextBind) return;
            _bindTries++;
            foreach (string n in Slots.Names) {
                if (n == RescanSlot && _aRescan == null) _aRescan = Slots.Require<Func<int>>(n);
            }
            _nextBind = Time.unscaledTime + 2f;
        }

        private static string RootDir() {
            return Path.Combine(Paths.ConfigPath, "SR_UCH", "profiles");
        }

        private static string SafeName(string s) {
            if (string.IsNullOrEmpty(s)) return "profile";
            var sb = new StringBuilder();
            foreach (char c in s) {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') continue;
                if (c == '.' && sb.Length == 0) continue;
                sb.Append(c);
            }
            string r = sb.ToString().Trim();
            if (r.Length > 48) r = r.Substring(0, 48);
            return r.Length == 0 ? "profile" : r;
        }

        private static string PathOf(string name) {
            return Path.Combine(RootDir(), SafeName(name) + ".profile");
        }

        // ================= 档案编解码 =================
        // 一行一个字段；XML 与配置快照用 Base64，避免换行把行格式撕开。

        private static string Encode(string s) {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string Decode(string s) {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? ""));
        }

        private static string SnapshotMods() {
            var sb = new StringBuilder();
            try {
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in _host.Config) {
                    ConfigDefinition d = kv.Key;
                    ConfigEntryBase e = kv.Value;
                    if (d == null || e == null || IsExcluded(d.Key)) continue;
                    string v;
                    try { v = Convert.ToString(e.BoxedValue, CultureInfo.InvariantCulture) ?? ""; }
                    catch (Exception __cx) { SRApi.Guard.Log("开局档案/读配置值", __cx); continue; }
                    sb.Append(d.Section.Replace('|', ' ')).Append('|')
                      .Append(d.Key.Replace('|', ' ')).Append('|')
                      .Append(v.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
                }
            } catch (Exception __ex) { SRApi.Guard.Log("开局档案/枚举配置", __ex); }
            return sb.ToString();
        }

        // README 明确「Reset defaults 刻意不动窗口尺寸/位置与侧栏宽」——
        // 档案套用沿用同一约定，内部状态键也不覆盖，免得把界面布局冲掉。
        private static bool IsExcluded(string key) {
            if (string.IsNullOrEmpty(key)) return true;
            string k = key.ToLowerInvariant();
            return k.Contains("window") || k.Contains("sidebar") || k.Contains("width") ||
                   k.Contains("height") || k.Contains("migrated") || k.Contains("col ") ||
                   k.Contains("x") && k.Length <= 2 || k.Contains("y") && k.Length <= 2 ||
                   k.Contains("internal") || k.Contains("migrat");
        }

        private static string CurrentRulesXml() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return "";
                GameRulePreset p = gs.GetCurrentRuleset();
                if (p == null) return "";
                return p.GetRulesetXmlString();
            } catch (Exception __ex) {
                SRApi.Guard.Log("开局档案/读当前规则", __ex);
                return "";
            }
        }

        private static string BuildProfile(string name) {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("name=").Append(name.Replace('\n', ' ')).Append('\n');
            sb.Append("time=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            try {
                GameSettings st = GameSettings.GetInstance();
                sb.Append("mode=").Append(st != null ? st.GameMode.ToString() : "").Append('\n');
            } catch (Exception __mx) { SRApi.Guard.Log("开局档案/读模式", __mx); }
            sb.Append("rules=").Append(Encode(CurrentRulesXml())).Append('\n');
            sb.Append("mods=").Append(_includeMods.Value ? Encode(SnapshotMods()) : "").Append('\n');
            return sb.ToString();
        }

        private static bool ParseProfile(string text, out string rulesXml, out string modsText, out string mode) {
            rulesXml = ""; modsText = ""; mode = "";
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextChars) return false;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != Header) return false;
            for (int i = 1; i < lines.Length; i++) {
                string l = lines[i];
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string k = l.Substring(0, eq), v = l.Substring(eq + 1);
                try {
                    if (k == "rules") rulesXml = Decode(v);
                    else if (k == "mods") modsText = v.Length > 0 ? Decode(v) : "";
                    else if (k == "mode") mode = v;
                } catch (Exception __dx) { SRApi.Guard.Log("开局档案/解码字段", __dx); return false; }
            }
            // 拒绝 DTD / 外部实体：规则 XML 由游戏自己生成，正常档案绝不含 DOCTYPE。
            if (!string.IsNullOrEmpty(rulesXml) && rulesXml.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return !string.IsNullOrEmpty(rulesXml);
        }

        // ================= 套用前差异 / 兼容性 =================

        /// <summary>把规则 XML 的 &lt;rules&gt; / &lt;mods&gt; 属性摊平成 name→value。</summary>
        /// <remarks>GameRulePreset.GetRulesetXml() 把全部规则与修改器都写成这两个节点的属性
        /// （:128229-128243、Modifiers.WriteToXmlNode :140207），所以属性名就是游戏版本的结构签名。
        /// 纯字符串 + XmlDocument，不用反射去猜枚举类型；XmlResolver 置 null 防外部实体。</remarks>
        private static Dictionary<string, string> RuleFields(string xml) {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(xml)) return d;
            try {
                var doc = new XmlDocument();
                doc.XmlResolver = null;
                doc.LoadXml(xml);
                XmlElement root = doc.DocumentElement;
                if (root == null) return d;
                foreach (XmlNode n in root.ChildNodes) {
                    if (n.Name != "rules" && n.Name != "mods") continue;
                    if (n.Attributes == null) continue;
                    foreach (XmlAttribute a in n.Attributes) d[n.Name + "." + a.Name] = a.Value;
                }
            } catch (Exception __xx) { SRApi.Guard.Log("开局档案/解析规则 XML", __xx); }
            return d;
        }

        private static int CountLines(string s) {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        /// <summary>生成「这次会改哪几项」；档案里有而当前游戏没有的字段名标 ⚠ 但不阻断。</summary>
        private static string[] BuildDiff(string rulesXml, string modsText) {
            var lines = new List<string>();
            _diffBlocked = false;
            string cur = CurrentRulesXml();
            if (string.IsNullOrEmpty(cur)) {
                lines.Add(SRApi.T("读不到当前规则，跳过逐项对比（不会阻止套用）",
                                  "Cannot read current rules; field-by-field diff skipped (does not block applying)"));
            } else {
                var now = RuleFields(cur);
                var want = RuleFields(rulesXml);
                int changed = 0, unknown = 0;
                foreach (KeyValuePair<string, string> kv in want) {
                    string old;
                    if (!now.TryGetValue(kv.Key, out old)) {
                        unknown++;
                        if (lines.Count < MaxDiffLines)
                            lines.Add(SRApi.T("⚠ 档案有、当前游戏 XML 无此字段：", "⚠ Field missing in this game version: ") + kv.Key);
                        _diffBlocked = true;
                    } else if (!string.Equals(old, kv.Value, StringComparison.Ordinal)) {
                        changed++;
                        if (lines.Count < MaxDiffLines)
                            lines.Add(kv.Key + "：" + old + " → " + kv.Value);
                    }
                }
                if (changed + unknown > MaxDiffLines)
                    lines.Add(SRApi.T("… 另有 ", "… plus ") + (changed + unknown - MaxDiffLines) + SRApi.T(" 项", " more"));
                if (changed == 0 && unknown == 0)
                    lines.Add(SRApi.T("规则与当前完全一致", "Rules already match the current game"));
            }
            if (_applyMods.Value && !string.IsNullOrEmpty(modsText))
                lines.Add(SRApi.T("本机将恢复 ", "Host config: restore ") + CountLines(modsText)
                          + SRApi.T(" 项 mod 配置", " entries"));
            else
                lines.Add(SRApi.T("不恢复 mod 配置", "Host config is left untouched"));
            return lines.ToArray();
        }

        /// <summary>算差异并（可选）进入二次确认态。非交互入口仍会把差异写进日志。</summary>
        private static void Preview(string text, bool armed) {
            _pendingText = text ?? "";
            _applyArmed = false;
            _parseOk = false;
            string rules, mods, mode;
            if (!ParseProfile(_pendingText, out rules, out mods, out mode)) {
                _diffBlocked = true;
                _diffTitle = SRApi.T("档案格式不对或已损坏", "Invalid or damaged profile");
                _diffLines = new string[0];
                SRApi.NotifyT("档案格式不对或已损坏", "Invalid or damaged profile");
                return;
            }
            _parseOk = true;
            _diffLines = BuildDiff(rules, mods);
            _diffTitle = _diffBlocked
                ? SRApi.T("⚠ 兼容性警告：档案里有当前游戏没有的规则字段，建议先看「设置 / 自检」",
                          "⚠ Compatibility warning: the profile has fields this game version lacks; check Settings / Self-check")
                : SRApi.T("将改动以下项目：", "About to change:");
            _applyArmed = armed;
            if (!armed) {
                try { SRApi.LogInfo("[开局档案] 套用前差异\n" + _diffTitle + "\n" + string.Join("\n", _diffLines)); }
                catch (Exception __lx) { SRApi.Guard.Log("开局档案/写差异日志", __lx); }
            }
        }

        private static string ReadProfile(string name) {
            string path = PathOf(name);
            if (!File.Exists(path)) { SRApi.NotifyT("找不到档案：{0}", "Profile not found: {0}", name); return null; }
            return File.ReadAllText(path);
        }

        // ================= 保存 / 套用 =================

        private static void SaveCurrent() {
            try {
                if (!_enabled.Value) return;
                string name = (_newName.Value ?? "").Trim();
                if (name.Length == 0) name = "Profile " + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string text = BuildProfile(name);
                Directory.CreateDirectory(RootDir());
                File.WriteAllText(PathOf(name), text, new UTF8Encoding(false));
                _status = SRApi.T("已保存档案：", "Saved profile: ") + name;
                SRApi.Notify(_status);
                Refresh();
                _sel = SafeName(name);
                _applyArmed = false;
                _diffLines = new string[0];
            } catch (Exception __ex) { SRApi.Guard.Log("开局档案/保存", __ex); }
        }

        private static void Refresh() {
            try {
                string dir = RootDir();
                var list = new List<string>();
                if (Directory.Exists(dir)) {
                    string[] files = Directory.GetFiles(dir, "*.profile", SearchOption.TopDirectoryOnly);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < files.Length && list.Count < MaxList; i++)
                        list.Add(Path.GetFileNameWithoutExtension(files[i]));
                }
                _items = list.ToArray();
            } catch (Exception __ex) { SRApi.Guard.Log("开局档案/列档案", __ex); }
        }

        private static string[] ListProfiles() {
            if (_items.Length == 0) Refresh();
            return _items;
        }

        /// <summary>聊天指令 / 外部调用：直接执行，但仍先算差异并写日志。</summary>
        private static void ApplyByName(string name) {
            try {
                if (string.IsNullOrEmpty(name)) return;
                string text = ReadProfile(name);
                if (text == null) return;
                Preview(text, false);
                if (!_parseOk) return;   // 格式损坏：Preview 已给过提示
                Execute(text);
            } catch (Exception __ex) { SRApi.Guard.Log("开局档案/按名套用", __ex); }
        }

        // 页面按钮 / 剪贴板导入的公共入口。交互路径先预览再确认；导入直接执行。
        private static void ApplyText(string text, bool confirm) {
            if (confirm && !_applyArmed) { Preview(text, true); return; }
            string use = confirm ? _pendingText : text;
            if (string.IsNullOrEmpty(use)) { _applyArmed = false; return; }
            Preview(use, false);      // 执行前重算一次，防止档案文件在此期间被改
            if (!_parseOk) { _applyArmed = false; return; }
            Execute(use);
            _applyArmed = false;
        }

        /// <summary>真正执行：配置快照 → 恢复 mod 配置 → 完整自检 → 广播规则 → 失败回滚。</summary>
        private static void Execute(string text) {
            try {
                if (!_enabled.Value) return;
                string rules, mods, mode;
                if (!ParseProfile(text, out rules, out mods, out mode)) {
                    SRApi.NotifyT("档案格式不对或已损坏", "Invalid or damaged profile");
                    return;
                }

                string before = SnapshotMods();
                int n = 0;
                if (_applyMods.Value && mods.Length > 0) n = ApplyMods(mods);

                // 自检握手放在广播之前：广播一旦发出就无法撤回，先拿到健康结论。
                int issues = -2;   // -2 = 未执行
                if (_autoCheck.Value && _aRescan != null) {
                    try { issues = _aRescan(); }
                    catch (Exception __sx) { SRApi.Guard.Log("开局档案/重扫自检", __sx); issues = -1; }
                }

                int sent = BroadcastRules(rules);
                bool failed = sent < 0 || issues == -1;
                if (failed && _rollback.Value && n > 0) {
                    ApplyMods(before);
                    n = 0;
                    _status = SRApi.T("套用失败，已把本机 mod 配置回滚到套用前", "Apply failed; host config rolled back");
                    SRApi.Notify(_status);
                    return;
                }

                string verdict;
                if (issues == -2) verdict = SRApi.T("自检未执行", "self-check skipped");
                else if (issues < 0) verdict = SRApi.T("自检重扫出错", "self-check rescan failed");
                else if (issues > 0) verdict = SRApi.T("自检 ", "self-check ") + issues + SRApi.T(" 项失效（请看设置 / 自检）", " broken (see Settings / Self-check)");
                else verdict = SRApi.T("自检全绿", "self-check clean");

                _status = SRApi.T("已套用档案（规则 ", "Applied profile (rules ")
                          + (sent == 1 ? SRApi.T("已广播", "broadcast")
                                       : sent == 0 ? SRApi.T("未广播（无联机房间）", "not broadcast (no online room)")
                                                   : SRApi.T("广播异常", "broadcast error"))
                          + SRApi.T("，配置恢复 ", ", config restored ") + n
                          + SRApi.T(" 项，", " entries, ") + verdict + SRApi.T("）", ")");
                SRApi.Notify(_status);
                try { SRApi.LogInfo("[开局档案] " + _status); }
                catch (Exception __lx) { SRApi.Guard.Log("开局档案/写结果日志", __lx); }
            } catch (Exception __ex) { SRApi.Guard.Log("开局档案/套用", __ex); }
        }

        private static int ApplyMods(string modsText) {
            int n = 0;
            string[] lines = modsText.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                string[] p = lines[i].Split('|');
                if (p.Length < 3 || IsExcluded(p[1])) continue;
                ConfigEntryBase e = FindEntry(p[0], p[1]);
                if (e == null) continue;
                try {
                    Type t = e.SettingType;
                    object v = t.IsEnum ? Enum.Parse(t, p[2], true)
                                       : Convert.ChangeType(p[2], t, CultureInfo.InvariantCulture);
                    e.BoxedValue = v;
                    n++;
                } catch (Exception __vx) { SRApi.Guard.Log("开局档案/写配置值", __vx); }
            }
            try { _host.Config.Save(); } catch (Exception __sx) { SRApi.Guard.Log("开局档案/保存配置", __sx); }
            return n;
        }

        private static ConfigEntryBase FindEntry(string section, string key) {
            try {
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in _host.Config) {
                    ConfigDefinition d = kv.Key;
                    if (d != null && d.Section == section && d.Key == key) return kv.Value;
                }
            } catch (Exception __fx) { SRApi.Guard.Log("开局档案/查配置项", __fx); }
            return null;
        }

        // 游戏自己的规则应用逻辑挂在 TabletRulesScreen 上（:111651），
        // 房主发消息后服务器转发给所有客户端，未装 mod 的房客由游戏写入。
        // 返回 1 = 已发、0 = 无联机房间（已知边界，不算失败）、-1 = 异常（触发回滚）。
        private static int BroadcastRules(string xml) {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.client == null || !NetworkClient.active) {
                    SRApi.Notify(SRApi.T("当前没有联机房间，规则部分未套用；请在树屋 / 房间内套用", "No online room: rules were not applied. Apply from the treehouse / lobby."));
                    return 0;
                }
                var msg = new MsgApplyRuleset();
                msg.rulesetXML = xml;
                msg.premadeIdx = -1;
                msg.applyRules = true;
                msg.applyPoints = true;
                msg.applyBlocks = true;
                msg.applyMods = true;
                msg.temporary = false;
                lm.client.Send(NetMsgTypes.ApplyRuleset, msg);
                return 1;
            } catch (Exception __bx) {
                SRApi.Guard.Log("开局档案/广播规则", __bx);
                return -1;
            }
        }

        private static string Summary() {
            if (!_enabled.Value) return null;
            return SRApi.T("开局档案 · ", "Match Profile · ") + ListProfiles().Length + SRApi.T(" 份", " saved");
        }

        // ================= 页面 =================

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            SRApi.Ctl.SectionTitle(SRApi.T("- 档案 -", "- Profiles -"));
            CfgRow(_enabled);
            CfgRow(_includeMods);
            CfgRow(_applyMods);
            CfgRow(_autoCheck);
            CfgRow(_rollback);
            CfgRow(_newName);

            string[] vals = new string[_items.Length + 1];
            string[] labels = new string[_items.Length + 1];
            vals[0] = "all";
            labels[0] = SRApi.T("选择一个档案", "Pick a profile");
            for (int i = 0; i < _items.Length; i++) { vals[i + 1] = _items[i]; labels[i + 1] = _items[i]; }
            SRApi.Ctl.FilterCombo("profile.list", vals, labels,
                () => _sel,
                v => { _sel = v; _applyArmed = false; _diffLines = new string[0]; },
                ref _openList, "all", SRApi.Ctl.Sc(240f));

            if (SRApi.Ctl.Button(SRApi.T("保存当前房间为档案", "Save current room"),
                                 SRApi.T("打包当前游戏规则 + 本 mod 配置", "Bundle current game rules and mod config")))
                SaveCurrent();
            if (_sel != "all") {
                string label = _applyArmed
                    ? SRApi.T("再点一次确认套用", "Tap again to confirm apply")
                    : SRApi.T("套用这个档案", "Apply this profile");
                if (SRApi.Ctl.Button(label,
                                     SRApi.T("第一次只列出会改哪些项目，第二次才真正套用", "First tap lists changes; second tap applies"))) {
                    if (_applyArmed) ApplyText(_pendingText, true);
                    else {
                        string text = ReadProfile(_sel);
                        if (text != null) Preview(text, true);
                    }
                }
                if (SRApi.Ctl.Button(SRApi.T("复制档案文本", "Copy profile text"),
                                     SRApi.T("复制到剪贴板，可贴给别人", "Copy to clipboard to share"))) {
                    try { GUIUtility.systemCopyBuffer = File.ReadAllText(PathOf(_sel)); }
                    catch (Exception __cx) { SRApi.Guard.Log("开局档案/复制", __cx); }
                }
            }
            if (SRApi.Ctl.Button(SRApi.T("从剪贴板导入", "Import from clipboard"),
                                 SRApi.T("读剪贴板里的档案文本并立刻套用", "Read a profile from the clipboard and apply it"))) {
                try { ApplyText(GUIUtility.systemCopyBuffer, false); }
                catch (Exception __ix) { SRApi.Guard.Log("开局档案/导入", __ix); }
            }
            if (SRApi.Ctl.Button(SRApi.T("重扫自检", "Rescan self-check"),
                                 SRApi.T("手动重跑一次完整自检，结果与套用后自动那次相同", "Rerun the full self-check; same as the automatic one after applying"))) {
                try {
                    int r = _aRescan != null ? _aRescan() : -1;
                    _status = r < 0 ? SRApi.T("自检通道不可用或重扫出错", "Self-check unavailable or rescan failed")
                                    : SRApi.T("自检重扫完成：", "Self-check rescanned: ") + r + SRApi.T(" 项失效", " broken");
                    SRApi.Notify(_status);
                } catch (Exception __rx) { SRApi.Guard.Log("开局档案/手动重扫", __rx); }
            }
            if (SRApi.Ctl.Button(SRApi.T("刷新列表", "Refresh list"), SRApi.T("重新扫描档案目录", "Re-scan the profile folder"))) Refresh();

            if (_sel != "all") {
                if (SRApi.Ctl.Button(_deleteArmed ? SRApi.T("再点一次确认删除", "Tap again to confirm delete")
                                                  : SRApi.T("删除这个档案", "Delete this profile"),
                                     SRApi.T("只删档案文件，不动当前配置", "Deletes the file only"))) {
                    if (_deleteArmed) {
                        try { File.Delete(PathOf(_sel)); } catch (Exception __dx) { SRApi.Guard.Log("开局档案/删除", __dx); }
                        _deleteArmed = false; _sel = "all"; _applyArmed = false; Refresh();
                    } else _deleteArmed = true;
                }
            }

            if (!string.IsNullOrEmpty(_diffTitle)) {
                SRApi.Ctl.SectionTitle(SRApi.T("- 套用前差异 -", "- Changes before applying -"));
                Line(_diffTitle, _diffTitle);
                for (int i = 0; i < _diffLines.Length; i++) Line(_diffLines[i], _diffLines[i]);
            }
            if (!string.IsNullOrEmpty(_status)) Line(_status, _status);

            SRApi.Ctl.SectionTitle(SRApi.T("- 说明 -", "- Notes -"));
            Line("规则部分走游戏原生 MsgApplyRuleset 广播，房客零安装也会生效；mod 配置只恢复房主机。",
                 "Rules use the game's own MsgApplyRuleset broadcast, so guests without the mod still get them; mod config is host-local.");
            Line("套用前会把档案里的规则字段与当前游戏生成的 XML 逐项对比：改名 / 消失的字段标 ⚠（只警告不阻断，旧档案仍可用），套用后自动重跑完整自检。",
                 "Before applying, profile rule fields are diffed against the XML this game version generates. Renamed or removed fields are flagged ⚠ (warning only), and a full self-check reruns afterwards.");
            Line("广播异常或自检重扫出错时会把本机 mod 配置回滚到套用前；「无联机房间」是已知边界，不算失败，仍会恢复 mod 配置。",
                 "A broadcast error or failed self-check rescan rolls host config back; 'no online room' is a known boundary, not a failure, and config is still restored.");
            Line("建议在树屋 / 开局前套用。对局中 TabletRulesScreen 是否仍在未实机确认；离线且没有联机连接时规则发不出去，只会恢复 mod 配置。",
                 "Apply from the treehouse / before a match. Whether TabletRulesScreen survives in-match is unverified; offline, only mod config is restored.");
            Line("窗口尺寸、侧栏宽度与内部状态键不写入 / 不覆盖，与「重置默认」的约定一致。档案文本用 Base64 承载 XML，导入时拒绝 DOCTYPE。",
                 "Window size, sidebar width and internal keys are excluded, matching Reset defaults. XML is Base64-encoded; DOCTYPE is rejected on import.");
        }
    }
}
