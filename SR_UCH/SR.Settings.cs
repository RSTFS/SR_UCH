// 设置页：条目行 / 控件渲染、滑块、下拉框、侧栏自定义顺序，以及 Configuration Manager 的排序与隐藏。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Settings（设置页 / 通用条目行渲染 / 控件渲染 / 滑块 / 下拉框）

        // 使 Configuration Manager 的条目顺序与 SR 界面一致：经反射替换 ConfigEntryBase.Description 的标签。
        // 顺序 = 侧栏栏目顺序（_internalSections），栏目内按条目绑定顺序；仅改标签，Value / AcceptableValues 保留。
        private static int _cmOrderVer = -1;   //上次应用排序时的条目版本
        private static readonly FieldInfo _fEntryDesc =
            typeof(ConfigEntryBase).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(ConfigEntryBase).GetField("Description", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void ApplyCmOrder() {
            if (_internalConfig == null) return;
            if (_cmOrderVer == _entryVersion) return;   //条目表没变：直接返回
            _cmOrderVer = _entryVersion;
            try {
                //Configuration Manager 为 OrderByDescending：栏目内倒序编号，先绑定的条目 Order 最大。
                Dictionary<string, int> secRank = new Dictionary<string, int>();
                //Order 大的在前，故侧栏第一个栏目 rank 取 总数-1-i。
                for (int i = 0; i < _internalSections.Count; i++) secRank[_internalSections[i]] = _internalSections.Count - 1 - i;
                //先按栏目分组，取得每栏条目数后才能倒序编号
                Dictionary<string, List<ConfigEntryBase>> bySec = new Dictionary<string, List<ConfigEntryBase>>();
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    if (e == null) continue;
                    string sec = e.Definition.Section;
                    if (string.IsNullOrEmpty(sec)) sec = "(General)";
                    List<ConfigEntryBase> list;
                    if (!bySec.TryGetValue(sec, out list)) { list = new List<ConfigEntryBase>(); bySec[sec] = list; }
                    list.Add(e);
                }
                foreach (KeyValuePair<string, List<ConfigEntryBase>> kv in bySec) {
                    int r; if (!secRank.TryGetValue(kv.Key, out r)) r = -1; //未注册为栏目的段 Order 最小，排最后
                    List<ConfigEntryBase> list = kv.Value;
                    for (int k = 0; k < list.Count; k++) {
                        ConfigEntryBase e = list[k];
                        int order = r * 1000 + (list.Count - 1 - k); //倒序编号：第 1 个绑定的排最前
                        try {
                            ConfigDescription d = e.Description;
                            if (_fEntryDesc != null) {
                                //Browsable=false + HideSetting=true：SR 自带界面，CM 中不再列出这些条目。
                                object[] tags = new object[] { new ConfigurationManagerAttributes { Order = order, Browsable = false, HideSetting = true } };
                                _fEntryDesc.SetValue(e, new ConfigDescription(d != null ? d.Description : "", d != null ? d.AcceptableValues : null, tags));
                            }
                        } catch (Exception __ex) { Guard.Log("设置条目显示顺序(单条)", __ex); }
                    }
                }
            } catch (Exception __ex) { Guard.Log("Configuration Manager 排序", __ex); }
        }

        //枚举下拉选项缓存：键为 entry 而非 Type —— RowEnumAllowed 的过滤结果依赖 entry，按 Type 缓存会串台。
        private static readonly Dictionary<ConfigEntryBase, object[]> _enumOptions = new Dictionary<ConfigEntryBase, object[]>();

        private static void SetValue(ConfigEntryBase entry, object value) {
            try {
                //类型归一化：int 条目必须收到 int，否则 BepInEx 抛异常导致滑块无效
                if (value != null) {
                    Type st = entry.SettingType;
                    if (st == typeof(int) && !(value is int)) value = Mathf.RoundToInt(Convert.ToSingle(value));
                    else if (st == typeof(float) && !(value is float)) value = Convert.ToSingle(value);
                }
                entry.BoxedValue = value;
                if (entry == _blockInputEntry) {
                    BlockInput = value is bool bv && bv;
                    ApplyEventSystemGate();
                }
                _dirtyConfig = entry.ConfigFile;
                _dirty = true;
                //外部改值时清空编辑框缓存；编辑框自身提交的那一帧不清，否则会吃掉小数点。
                if (_editSelfFrame != Time.frameCount) _editText.Remove(entry);
            } catch (Exception ex) {
                SR.LogWarn("配置修改失败: " + ex.Message);
            }
        }

        private static List<ConfigEntryBase> InternalSectionEntries() {
            //复用 SectionEntries 的按版本缓存（过滤规则一致：段名 + RowVisible）。
            return SectionEntries(_selectedInternalSection);
        }

        //设置页条目按类别的分组顺序，与 config 绑定顺序一致，插件组最后。
        private static readonly string[][] _settingsGroups = new string[][] {
            new[] { "按键", Keys.OpenKey, Keys.BlockInput },
            new[] { "界面", Keys.UiScale, Keys.WinWidth, Keys.WinHeight, Keys.WinX, Keys.WinY, Keys.Language },
            new[] { "插件", Keys.DisabledPlugins },
        };

        private static string SettingsGroup(string key) {
            foreach (string[] g in _settingsGroups) {
                for (int i = 1; i < g.Length; i++) {
                    if (g[i] == key) return g[0];
                }
            }
            return null;
        }

        private static void RenderSettingsEntries(float colWidth) {
            string lastGroup = null;
            bool any = false;
            bool pluginListDrawn = false;
            foreach (ConfigEntryBase entry in SettingsEntries()) {
                any = true;
                //栏目宽度专用行：滑块 + [-]/[+] 步进
                if (entry.Definition.Key == Keys.SidebarWidth) { RenderSidebarWidthRow(); continue; }
                string g = SettingsGroup(entry.Definition.Key);
                //外部插件清单排在"界面"分组之后
                if (!pluginListDrawn && lastGroup == "界面" && (g == null || g != "界面")) {
                    pluginListDrawn = true;
                    GUILayout.Space(Sc(4));
                    RenderSidebarOrder();
                    RenderPluginList();
                }
                if (g != null && g != lastGroup) {
                    lastGroup = g;
                    GUILayout.Label("- " + T(g, g == "按键" ? "Keys" : g == "界面" ? "UI" : "Plugins") + " -", _secHeader);
                }
                RenderEntryRow(entry, true, colWidth);
            }
            if (!pluginListDrawn) {
                GUILayout.Space(Sc(4));
                RenderSidebarOrder();
                RenderPluginList();
            }
            if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
            RenderAuditBlock();   //设置页底部的自检块
        }

        //自检块：显示启动自检结果（补丁目标 + 反射成员 + 扩展点 / 孤儿键 / 运行时异常）。
        //置于设置页：属诊断信息，不占侧栏栏目。
        private static void RenderAuditBlock() {
            try {
                GUILayout.Space(Sc(6));
                GUILayout.Label("- " + T("自检", "Self-check") + " -", _secHeader);
                GUILayout.Label(T("按名绑定的补丁目标", "Named patch targets") + "："
                    + SR.AuditPatchOk + " / " + SR.AuditPatchTotal, _labelWrap);
                GUILayout.Label(T("反射成员", "Reflected members") + "：" + SR.AuditRefOk + " / " + SR.AuditRefTotal, _labelWrap);
                System.Collections.Generic.IList<SR.AuditIssue> issues = SR.AuditIssues;
                if (issues == null || issues.Count == 0) {
                    GUILayout.Label(T("按名绑定的目标全部有效。", "All name-bound targets are valid."), _labelWrap);
                } else {
                    GUILayout.Label(T("以下目标已失效 -> 对应功能不会生效（开关还在、行为没了）：",
                        "These targets no longer exist - the listed features will NOT work:"), _labelWrap);
                    for (int i = 0; i < issues.Count; i++) {
                        //AuditIssue.Kind 取值是中文（"补丁" / "反射"），须映射为当前语言；未知取值原样输出。
                        string kindRaw = issues[i].Kind;
                        string kindTxt = kindRaw == "补丁" ? T("补丁", "Patch")
                                       : kindRaw == "反射" ? T("反射", "Reflection")
                                       : kindRaw;
                        GUILayout.Label("   ✗  " + kindTxt + "  " + issues[i].Target + "   <- " + issues[i].Scope, _labelWrap);
                    }
                }
                //显示当前生效的扩展点数量，便于外部模块对接时确认是否接上。
                GUILayout.Label(T("扩展点", "Extension points") + "："
                    + T("槽位 ", "slots ") + Slots.Count
                    + T("（", " (") + string.Join(", ", new List<string>(Slots.Names).ToArray()) + ")"
                    + T("，覆盖层 ", ", overlays ") + SR.OverlayCount
                    + T("，UI 阻断 ", ", UI blockers ") + SR.UiBlockProviderCount, _labelWrap);
                if (Slots.MissCount > 0) {
                    GUILayout.Label(T("没人提供的槽位", "Unclaimed slots") + "：" + Slots.MissCount
                        + T("（取用时返回 null，详见日志", " (Require returned null; see log)") + "："
                        + string.Join(", ", new List<string>(Slots.MissNames).ToArray()) + ")", _labelWrap);
                }
                if (SR.OrphanKeyCount > 0) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(T("配置里的孤儿键", "Orphan config keys") + "：" + SR.OrphanKeyCount
                        + T("（没有代码再读它；详见日志）", " (nothing reads them any more; see log)"), _labelWrap);
                    //一键清理这些键（原子替换，原文件备份为 .bak）
                    if (GUILayout.Button(T("清理", "Clean up"), _btn, GUILayout.Width(Sc(64)), GUILayout.Height(Sc(24)))) {
                        int cleaned = SR.CleanOrphanKeys();
                        if (cleaned > 0) SR.Notify(SR.T("已清理 " + cleaned + " 个孤儿键（原文件已备份为 .bak）",
                                                        "Cleaned " + cleaned + " orphan keys (previous file kept as .bak)"));
                        else SR.Notify(SR.T("没有可清理的孤儿键，或读不到 BepInEx 的孤儿表", "Nothing to clean, or the orphan table is unavailable"));
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
                if (ConfigMigration.RuleConflictCount > 0) {
                    GUILayout.Label(T("迁移规则冲突", "Migration rule conflicts") + "：" + ConfigMigration.RuleConflictCount
                        + T("（会让某些设置每次启动被删/读不到；详见日志", " (some settings get deleted or unreadable every launch; see log)"), _labelWrap);
                }
                if (SR.FallbackSectionCount > 0) {
                    GUILayout.Label(T("没有归属的配置段", "Unowned config sections") + "：" + SR.FallbackSectionCount
                        + T("（会在侧栏以兜底页出现；详见日志", " (shown as fallback pages; see log)"), _labelWrap);
                }
                //显示运行期异常计数与最近一条，弥补启动自检只能看到静态失效的缺口。
                if (SR.Guard.WarnCount > 0) {
                    GUILayout.Label(T("运行时异常次数", "Runtime errors") + "：" + SR.Guard.WarnCount, _labelWrap);
                    if (!string.IsNullOrEmpty(SR.Guard.LastWhat)) {
                        GUILayout.Label(T("最近一次", "Last") + "：" + SR.Guard.LastWhat
                            + " - " + (SR.Guard.LastError ?? ""), _labelWrap);
                    }
                }
            } catch (Exception __ex) { Guard.Log("渲染自检块", __ex); }
        }

        private static void RenderSidebarWidthRow() {
            int cur = _sidebarWEntry != null ? Mathf.Clamp(_sidebarWEntry.Value, Ui.SidebarWMin, Ui.SidebarWMax) : 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(T("栏目宽度", "Sidebar Width"), T("左侧栏目栏宽度（0 = 自动，100-320）", "Left sidebar width (0 = auto, 100-320)")), _label, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(26)));
            if (GUILayout.Button("-", _btn, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) SetSidebarWidth(cur - 16);
            Rect sr = GUILayoutUtility.GetRect(Sc(150), CtlH(), GUILayout.Width(SliderTrackWidth()));
            float nv = DrawSlider(sr, cur, (float)Ui.SidebarWMin, (float)Ui.SidebarWMax, true);
            if (Mathf.Abs(nv - cur) > 0.0001f) SetSidebarWidth(Mathf.RoundToInt(nv)); //值变化即写
            if (GUILayout.Button("+", _btn, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) SetSidebarWidth(cur + 16);
            GUILayout.Label(cur + " px", _label, GUILayout.Width(Sc(70)), GUILayout.Height(Sc(26)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        //同时更新即时值与配置项（布局立即生效并持久化）。
        private static void SetSidebarWidth(int px) {
            px = Mathf.Clamp(px, 0, 320);
            _sidebarW = px;
            if (_sidebarWEntry != null) _sidebarWEntry.Value = px;
        }

        //设置页条目列表：按条目版本缓存；比 SectionEntries 多一条组合键内部条目过滤，故单独缓存。
        private static List<ConfigEntryBase> _settingsCache;
        private static int _settingsCacheVer = -1;
        private static List<ConfigEntryBase> SettingsEntries() {
            if (_settingsCache != null && _settingsCacheVer == _entryVersion) return _settingsCache;
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            if (_internalConfig != null) {
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    if (e.Definition.Section != "Settings") continue;
                    //这些项在会话页 / 搜索栏已有开关，设置页不再渲染（可见性由功能经 SRApi.RowFilters 注册）
                    if (!SR.RowVisible(e)) continue;
                    //组合键修饰键的内部持久化条目（RegisterComboEntry 建立），不在设置页显示
                    if (e.Definition.Key.StartsWith(Keys.ComboPrefix)) continue;
                    if (e.Definition.Key == Keys.SidebarOrder) continue; //侧栏顺序由拖动维护
                    result.Add(e);
                }
            }
            _settingsCache = result;
            _settingsCacheVer = _entryVersion;
            return result;
        }

        //可点击的条目标签：左键恢复默认值，右键绑定快捷键；说明固定 3 行，第 4 行由核心追加默认值 / 按键。
        //hotkey 为 null 时仅 bool 条目接入 "row:段.键" 自动快捷键。
        private static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey = null) {
            GUIContent c2 = content;
            ConfigEntry<KeyCode> hk = hotkey; //只认功能显式传入的键
            try {
                string tip = content.tooltip != null ? content.tooltip : "";
                //第 4 行：已绑键时显示“按键：X”，否则显示“默认：X”；都没有则不追加。
                //右键提示并入同一行，保证悬浮提示仍为 3 + 1 行。
                string extra = "";
                if (entry != null) {
                    if (hk != null) extra = KeyTipSuffix(hk);
                    if (string.IsNullOrEmpty(extra)) extra = DefaultSuffix(entry);
                }
                if (!string.IsNullOrEmpty(extra)) {
                    if (hk != null) extra += T("（右键绑定/删除）", " (right-click to bind/remove)");
                    tip += (tip.Length > 0 ? "\n" : "") + extra;
                }
                if (hk != null) {
                    //绑定的键（录制中为 [...]）跟在标签后，宽度按需放宽
                    string txt = content.text + KeySuffix(hk);
                    c2 = new GUIContent(txt, tip);
                } else if (tip != content.tooltip) {
                    c2 = new GUIContent(content.text, tip);
                }
                //宽度只按文字实际宽度 + 内边距占位，只收缩不放大；带键位后缀时按需放宽到窗口可用宽度。
                float need = _label.CalcSize(c2).x + Sc(8);
                if (need < w) w = Mathf.Max(Sc(24), need);
                else if (hk != null) w = Mathf.Min(need, Mathf.Max(w, _winWidth - SidebarWidth() - Sc(24)));
            } catch (Exception __ex) { Guard.Log("条目默认值处理", __ex); }
            if (GUILayout.Button(c2, _label, GUILayout.Width(w), GUILayout.Height(h))) {
                //只认左键：右键用于绑定快捷键，不可同时重置默认值
                Event evLbl = Event.current;
                if (evLbl == null || evLbl.button == 0) {
                    if (entry != null) { try { entry.BoxedValue = entry.DefaultValue; } catch (Exception __ex) { Guard.Log("恢复条目默认值", __ex); } }
                    MarkKeyChanged();   //重置可能改动键位，令序列监听列表失效
                    _editText.Remove(entry);
                    _editOpen.Remove(entry);
                    if (_capturing == entry) _capturing = null;
                }
            }
            //右键命中区：标签 + 右侧 44px
            if (hk != null) RegisterRowHotkey(hk);
        }

        private static ConfigFile _visCfg;
        private static int _visVer = -1;
        private static List<ConfigEntryBase> _visCache;
        private static List<ConfigEntryBase> VisibleEntries(ConfigFile config) {
            if (config == null) return new List<ConfigEntryBase>();
            //按条目版本缓存（外部插件页每个 OnGUI 事件都会调用）。
            if (_visCache != null && _visCfg == config && _visVer == _entryVersion) return _visCache;
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            foreach (ConfigEntryBase e in AllEntries(config)) {
                result.Add(e);
            }
            _visCfg = config; _visVer = _entryVersion; _visCache = result;
            return result;
        }

        //取配置条目：优先用 ConfigFile 显式实现的 IDictionary.Values（GetConfigEntries 已过时）。
        //未实现该接口的旧版 BepInEx 回退到过时方法。
        private static readonly ConfigEntryBase[] NoEntries = new ConfigEntryBase[0];
        private static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) {
            if (config == null) return NoEntries;
            IDictionary<ConfigDefinition, ConfigEntryBase> dict = config as IDictionary<ConfigDefinition, ConfigEntryBase>;
            if (dict != null) return dict.Values;
#pragma warning disable CS0618 //旧版 BepInEx 兜底
            return config.GetConfigEntries();
#pragma warning restore CS0618
        }

        //侧栏宽度：0 = 自动，>0 = 用户拖动设定的宽度。
        private static float _sidebarW = 0f;
        private static bool _sidebarResizing;
        private static ConfigEntry<int> _sidebarWEntry; //设置页栏目宽度滑块（0 = 自动）
        private static ConfigEntry<string> _sidebarOrderEntry; //侧栏自定义顺序（逗号分隔），拖动列头时写回
        //侧栏栏目拖拽排序不做坐标命中判定：条目画在 BeginArea / ScrollView 内，鼠标事件为屏幕坐标，两者换算不稳定。
        //改用 GUIUtility.hotControl 判定按下项，并以鼠标纵向位移 ÷ 行高判定目标行。

        //按配置中的自定义顺序重排侧栏栏目，未记录的新栏目排到末尾。
        //本方法是唯一产出三份列表的地方：_allSections（全量）、_userHiddenSections（"!" 前缀）、_internalSections（显示项）。
        private static void ApplyCustomSidebarOrder() {
            try {
                string ord = _sidebarOrderEntry != null ? _sidebarOrderEntry.Value : null;
                List<string> all = new List<string>();
                HashSet<string> hid = new HashSet<string>();
                //先按改名映射改写段名，再做死条目判定：栏目改名后旧串里的旧名匹配不到真实栏目。
                if (!string.IsNullOrEmpty(ord)) ord = RemapSidebarOrderNames(ord);
                //剔除匹配不到真实栏目的死条目，须在字母序判定之前完成。
                if (!string.IsNullOrEmpty(ord)) ord = DropDeadSidebarOrderNames(ord);
                //识别纯字母序的 Sidebar Order（非用户自定义）并忽略，避免其永久覆盖默认 rank 顺序。
                if (!string.IsNullOrEmpty(ord)) {
                    List<string> probe = new List<string>();
                    foreach (string raw0 in ord.Split(',')) {
                        string n0 = raw0.Trim();
                        if (n0.Length == 0) continue;
                        if (n0[0] == '!') n0 = n0.Substring(1).Trim();
                        if (n0.Length > 0 && !probe.Contains(n0)) probe.Add(n0);
                    }
                    List<string> sorted = new List<string>(probe);
                    //须用 OrdinalIgnoreCase：改名后段名大小写可能变化，Ordinal 会漏判字母序。
                    sorted.Sort(delegate(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase); });
                    bool alphabetical = probe.Count > 1;
                    for (int i = 0; alphabetical && i < probe.Count; i++)
                        if (string.Compare(probe[i], sorted[i], StringComparison.OrdinalIgnoreCase) != 0) alphabetical = false;
                    if (alphabetical) ord = null;   //视为"无自定义"-> 回到默认 rank 顺序
                }
                if (!string.IsNullOrEmpty(ord)) {
                    foreach (string raw in ord.Split(',')) {
                        string n = raw.Trim();
                        if (n.Length == 0) continue;
                        bool h = n[0] == '!';
                        if (h) n = n.Substring(1).Trim();
                        if (n.Length == 0 || all.Contains(n)) continue;
                        all.Add(n);
                        if (h) hid.Add(n);
                    }
                }
                //不再按键名字母序补位：RebuildSections 已按各功能注册的 SR.Nav(键, rank) 排好默认顺序。
                for (int i = 0; i < _internalSections.Count; i++) {
                    string s2 = _internalSections[i];
                    if (!all.Contains(s2)) all.Add(s2);
                }
                _allSections.Clear();
                for (int i = 0; i < all.Count; i++) _allSections.Add(all[i]);
                _userHiddenSections.Clear();
                foreach (string h2 in hid) _userHiddenSections.Add(h2);
                _internalSections.Clear();
                for (int i = 0; i < all.Count; i++) if (!_userHiddenSections.Contains(all[i])) _internalSections.Add(all[i]);
                //清洗结果与默认值一致时落盘清除死条目，避免每帧重演；正常自定义顺序不会被抹掉。
                string cleaned = string.Join(",", _internalSections.ToArray());
                if (_sidebarOrderEntry != null && !string.IsNullOrEmpty(ord)
                    && _sidebarOrderEntry.Value != null && _sidebarOrderEntry.Value != cleaned
                    && DropDeadSidebarOrderNames(_sidebarOrderEntry.Value) == cleaned)
                    _sidebarOrderEntry.Value = cleaned;
            } catch (Exception __ex) { Guard.Log("应用侧栏自定义顺序", __ex); }
        }

        //侧栏顺序串中段名的改名映射（旧名 -> 新名）。与 ConfigMigration.SectionMap 不同：后者迁移 cfg 段名。
        private static readonly Dictionary<string, string> SidebarOrderRenames = new Dictionary<string, string> {
            { "建造增强", "build" },
            { "Builder Enhancements", "build" },
        };

        //按映射改写 Sidebar Order 中的旧段名，保留 ! 前缀与原顺序。
        private static string RemapSidebarOrderNames(string ord) {
            if (string.IsNullOrEmpty(ord)) return ord;
            List<string> parts = new List<string>();
            bool changed = false;
            foreach (string raw in ord.Split(',')) {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                bool h = n[0] == '!';
                if (h) n = n.Substring(1).Trim();
                if (n.Length == 0) continue;
                string mapped;
                if (SidebarOrderRenames.TryGetValue(n, out mapped) && mapped != n) {
                    n = mapped;
                    changed = true;
                }
                parts.Add(h ? ("!" + n) : n);
            }
            return changed ? string.Join(",", parts.ToArray()) : ord;
        }

        //剔除 Sidebar Order 中匹配不到真实栏目的死条目；判定依据为 _internalSections。
        private static string DropDeadSidebarOrderNames(string ord) {
            if (string.IsNullOrEmpty(ord)) return ord;
            HashSet<string> live = new HashSet<string>(_internalSections);
            if (live.Count == 0) return ord;   //还没算出真实栏目，别贸然清洗
            List<string> parts = new List<string>();
            foreach (string raw in ord.Split(',')) {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                bool h = n[0] == '!';
                if (h) n = n.Substring(1).Trim();
                if (n.Length == 0) continue;
                if (!live.Contains(n)) continue;   //死条目：直接丢掉，不占位
                parts.Add(h ? ("!" + n) : n);
            }
            return string.Join(",", parts.ToArray());
        }

        // 设置页的栏目顺序块：按住列头拖动调整顺序（用左键，IMGUI 对左键的拖动 / 松开事件可靠）。
        // 排序状态由 SRApi.TableArgs 持有（见 EnsureOrdArgs / RenderSidebarOrder）。

        // 自定义栏目 = 无数据行的 SR 列表，只用其表头：列头即侧栏栏目名，拖动改顺序，右键隐藏，Shift+右键还原。
        private static readonly List<int> _ordVis = new List<int>();
        private static readonly HashSet<int> _ordHidden = new HashSet<int>();
        // 拖拽与列宽状态是 TableArgs 的实例字段，须跨帧持有。
        private static SRApi.TableArgs _ordArgs;
        private static int _ordN = -1;
        private static float[] _ordColW;
        private static float[] _ordDefaultColW() { int n = Mathf.Max(1, _ordN); float[] d = new float[n]; for (int i = 0; i < n; i++) d[i] = 64f; return d; }
        private static int[] _ordOrder;   //显示顺序（数据列下标）
        private static Vector2 _ordScroll;   //列表内部的滚动位置
        private static void EnsureOrdArgs(int n) {
            if (_ordArgs != null && _ordN == n) return;
            _ordN = n;
            _ordArgs = new SRApi.TableArgs();
            _ordColW = new float[n];
            for (int i = 0; i < n; i++) _ordColW[i] = 64f;   //表头默认宽度 64px（可拖分隔线拉宽）
            _ordArgs.colW = _ordColW;
            _ordArgs.defaultColW = _ordDefaultColW();   //Shift+右键还原列宽用
            _ordArgs.rowCount = 0;        //无数据行，只用表头
            _ordArgs.emptyHint = null;
            _ordArgs.headerH = Sc(22); _ordArgs.rowH = Sc(22); _ordArgs.rowsH = 0f;
            _ordArgs.vertical = false;
            _ordArgs.hideable = true; _ordArgs.reorderable = true; _ordArgs.resizable = true;
            _ordArgs.vis = _ordVis; _ordArgs.hidden = _ordHidden;
            //当前显示顺序
            _ordOrder = new int[n];
            for (int i = 0; i < n; i++) _ordOrder[i] = i;
            _ordArgs.order = _ordOrder;
            _ordArgs.allCols = new List<int>();
            _ordArgs.headerCols = new List<int>();
            for (int i = 0; i < n; i++) { _ordArgs.allCols.Add(i); _ordArgs.headerCols.Add(i); }
            _ordArgs.title = delegate(int ci) { return (ci >= 0 && ci < _ordN) ? ZhSection(SR._allSections[ci]) : ""; };
            _ordArgs.applyOrder = delegate(int[] o) {
                try {
                    if (o == null || o.Length != _ordN || SR._sidebarOrderEntry == null) return;
                    //核心重建后会以当前顺序回调 applyOrder 做归一化，相同则跳过，避免把显示顺序固化进配置。
                    bool same = _ordOrder != null && _ordOrder.Length == o.Length;
                    for (int i = 0; same && i < o.Length; i++) if (_ordOrder[i] != o[i]) same = false;
                    if (same) return;
                    string[] names = new string[_ordN];
                    for (int i = 0; i < _ordN; i++) names[i] = SR._allSections[o[i]];
                    SR._sidebarOrderEntry.Value = string.Join(",", names);
                    //o 是列下标置换，RebuildSections 随后会按新序重建 _allSections；
                    //保留旧置换会让下一帧用过期的列映射，故置空，下次绘制时重建为恒等序。
                    _ordOrder = null;   //置空 -> EnsureOrdArgs/下一次绘制时重建为恒等序
                    //写完顺序立即重建侧栏，否则 _internalSections 仍为旧序
                    SR.RebuildSectionsPublic(false);
                } catch (Exception __ex) { Guard.Log("保存栏目顺序", __ex); }
                };
            _ordArgs.resetAll = delegate { SR.ResetSidebarOrder(); };   //Shift+右键 = 还原全部（排序 + 隐藏）
            _ordArgs.saveWidths = delegate { };   //列宽由静态 _ordColW 跨帧保留
        }

        private static void RenderSidebarOrder() {
            try {
                if (SR._allSections.Count <= 1) return;
                GUILayout.Label(T("- 自定义栏目 -", "- Custom sections -"), _secHeader);
                SR.WrapLabel(T("拖动列头调整侧栏顺序；右键列头隐藏栏目；Shift+右键还原。",
                                  "Drag a header to reorder; right-click a header to hide a section; Shift+right-click to restore."));
                int n = SR._allSections.Count;
                EnsureOrdArgs(n);
                //高度贴合内容：横向列头仅 Sc(24) 高，横向溢出由内部滚动兜住。
                GUILayout.BeginVertical(GUILayout.Height(Sc(30)));
                SRApi.TableArgs a = _ordArgs;
                _ordHidden.Clear(); _ordVis.Clear();
                for (int i = 0; i < n; i++) {
                    if (SR._userHiddenSections.Contains(SR._allSections[i])) _ordHidden.Add(i); else _ordVis.Add(i);
                }
                //sum = 可见列头的总宽（列头横向排列），作为盒宽传给 GetRect；溢出由内部滚动兜住。
                float sum = 0f; for (int i = 0; i < _ordVis.Count; i++) sum += SR.Ctl.Sc(_ordColW[_ordVis[i]]);
                if (sum < Sc(60)) sum = Sc(60);
                GUILayout.Space(Sc(2));
                //列表内部滚动：滚动条隐藏，滚轮仍可用
                _ordScroll = GUILayout.BeginScrollView(_ordScroll, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none);
                a.box = GUILayoutUtility.GetRect(sum, Sc(24));   //横向列头：高度固定 24px
                a.stripW = sum;
                if (_ordOrder == null) { _ordOrder = new int[n]; for (int i = 0; i < n; i++) _ordOrder[i] = i; a.order = _ordOrder; }   //恒等序兜底
                a.mp = Event.current != null ? Event.current.mousePosition : Vector2.zero;
                SR.TableDraw(a);
                GUILayout.EndScrollView();
                for (int i = 0; i < n; i++) {
                    bool hid = _ordHidden.Contains(i);
                    if (hid != SR._userHiddenSections.Contains(SR._allSections[i])) SR.SetSectionHidden(SR._allSections[i], hid);
                }
                GUILayout.EndVertical();
                GUILayout.Space(Sc(4));
            } catch (Exception __ex) { Guard.Log("自定义栏目", __ex); }
        }
        private static float SidebarWidth() {
            //拖动中读即时值 _sidebarW，否则读栏目宽度配置项
            float w = 0f;
            if (_sidebarResizing) w = _sidebarW;
            if (w <= 0f && _sidebarWEntry != null) w = _sidebarWEntry.Value;
            if (w <= 0f) w = _sidebarW;
            //先判 w 再算自动宽度：CalcAutoSidebarWidth 需逐项 CalcSize，而本方法每帧被调用十几次。
            if (w > 0f) return Mathf.Clamp(w, 100f, 320f);
            return CalcAutoSidebarWidth();
        }

        private static int _autoSbKey;
        private static float _autoSbW = -1f;
        private static float CalcAutoSidebarWidth() {
            //按集合 / 语言 / 缩放缓存；缓存键用哈希折叠，避免每帧重建长字符串。
            int key = 17;
            key = key * 31 + _internalSections.Count;
            for (int i = 0; i < _internalSections.Count; i++) key = key * 31 + _internalSections[i].GetHashCode();
            key = key * 31 + _externalPlugins.Count;
            for (int i = 0; i < _externalPlugins.Count; i++) key = key * 31 + _externalPlugins[i].name.GetHashCode();
            key = key * 31 + SR.LangVer;
            key = key * 31 + Mathf.RoundToInt(_scaled * 100f);
            if (key == _autoSbKey) return _autoSbW;
            float maxW = 0f;
            foreach (string s in _internalSections) {
                maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(ZhSection(s))).x);
            }
            foreach (PluginEntry p in _externalPlugins) {
                maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(p.name)).x);
            }
            _autoSbKey = key;
            _autoSbW = Mathf.Clamp(maxW + 26f, 110f, 260f);
            return _autoSbW;
        }

        //当前 RenderControl 的调用来源是否为内部条目（滑块路径据此决定是否绘制同行附加控件）。
        private static bool _curIsInternal = true;
        //同行附加控件的本帧已绘制记录（条目 + 帧号），防重复渲染。
        private static ConfigEntryBase _followDoneEntry;
        private static int _followDoneFrame = -1;

        private static float EntryNameWidth() {
            //按 模式 / 分区 / 语言版本 / 条目版本 / 缩放 / 外部插件 哈希缓存列宽，避免每帧 CalcSize。
            //必须含 LangVer（外部语言包下 _langEn 恒为 false）与 _entryVersion（惰性绑定会新增条目）。
            PluginEntry __pe = CurrentExternalPlugin();
            int key = 17;
            key = key * 31 + _mode.GetHashCode();
            key = key * 31 + _selectedInternalSection.GetHashCode();
            key = key * 31 + SR.LangVer;
            key = key * 31 + _entryVersion;
            key = key * 31 + Mathf.RoundToInt(_scaled * 100f);
            key = key * 31 + (__pe != null ? __pe.guid.GetHashCode() : 0);
            if (key == _nameWKey) return _nameWCached;
            _nameWKey = key;
            float maxW = 60f;
            if (_mode == Mode.Internal) {
                foreach (ConfigEntryBase e in InternalSectionEntries()) maxW = Mathf.Max(maxW, _nameLabel.CalcSize(new GUIContent(ZhKey(e))).x);
            } else if (_mode == Mode.Settings) {
                foreach (ConfigEntryBase e in SettingsEntries()) maxW = Mathf.Max(maxW, _nameLabel.CalcSize(new GUIContent(ZhKey(e))).x);
            } else {
                ConfigFile cfg = CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null;
                foreach (ConfigEntryBase e in VisibleEntries(cfg)) maxW = Mathf.Max(maxW, _nameLabel.CalcSize(new GUIContent(e.Definition.Key)).x);
            }
            _nameWCached = Mathf.Clamp(maxW + 16f, 90f, 240f);
            return _nameWCached;
        }

        //名称实测宽度按条目缓存（_nameLabel 为 wordWrap 样式，测量较贵；一帧至少 Layout + Repaint 两趟）。
        private static readonly Dictionary<ConfigEntryBase, float> _nameTextW = new Dictionary<ConfigEntryBase, float>();
        private static int _nameTextWVer = -1;
        private static float NameTextWidth(ConfigEntryBase entry, string name) {
            int ver = LangVer * 1000 + Mathf.RoundToInt(_scaled * 100f);
            if (_nameTextWVer != ver) { _nameTextW.Clear(); _nameTextWVer = ver; }
            float w;
            if (_nameTextW.TryGetValue(entry, out w)) return w;
            w = _nameLabel.CalcSize(new GUIContent(name)).x;
            _nameTextW[entry] = w;
            return w;
        }

        //某 section 的可见条目（供功能自绘页拼装页面，自动应用功能注册的行可见性）。
        //按 section 缓存，条目表版本变化时整体失效。
        private static readonly Dictionary<string, List<ConfigEntryBase>> _secCache = new Dictionary<string, List<ConfigEntryBase>>();
        private static readonly Dictionary<string, int> _secCacheVer = new Dictionary<string, int>();
        //兜底自检：每 30 帧抽查一次 ConfigFile 的条目总数，变化即让所有按版本缓存的表失效。
        //NoteEntryBound 负责当帧立即失效，这里是最迟半秒的保险。
        private static int _cfgCountSeen = -1;
        private static int _cfgCountAtFrame = -9999;
        private static void SyncEntryVersionFromConfig() {
            try {
                ConfigFile cfg = _internalConfig;
                if (cfg == null) return;
                if (Time.frameCount - _cfgCountAtFrame < 30) return;
                _cfgCountAtFrame = Time.frameCount;
                IDictionary<ConfigDefinition, ConfigEntryBase> d = cfg as IDictionary<ConfigDefinition, ConfigEntryBase>;
                if (d == null) return;
                int n = d.Count;
                if (n == _cfgCountSeen) return;
                bool first = _cfgCountSeen < 0;
                _cfgCountSeen = n;
                if (!first) _entryVersion++;   //首次仅记录基线
            } catch { }
        }

        //public：功能（独立程序集）按段取条目表以渲染自己的页面。
        public static List<ConfigEntryBase> SectionEntries(string section) {
            SyncEntryVersionFromConfig();
            int ver = _entryVersion;
            List<ConfigEntryBase> cached;
            int oldVer;
            //两步显式判定，不依赖 && 的求值顺序；cached != null 用于排除字典中值为 null 的项。
            bool haveCache = _secCache.TryGetValue(section, out cached);
            bool haveVer = _secCacheVer.TryGetValue(section, out oldVer);
            if (haveCache && haveVer && oldVer == ver && cached != null) return cached;
            cached = new List<ConfigEntryBase>();
            ConfigFile cfg = SR.Ctl.InternalConfig;
            if (cfg == null) return cached;
            foreach (ConfigEntryBase e in AllEntries(cfg)) {
                if (e.Definition.Section != section) continue;
                if (!SR.RowVisible(e)) continue;
                cached.Add(e);
            }
            _secCache[section] = cached;
            _secCacheVer[section] = ver;
            return cached;
        }

        //条目表版本号与按 "段\t键" 的查找缓存。
        //组合键 / [Reflection] / 自动快捷键条目为惰性 Bind：未命中回退全表扫描，且只缓存命中结果。
        private static readonly Dictionary<string, ConfigEntryBase> _entryCache = new Dictionary<string, ConfigEntryBase>();
        private static int _entryVersion;

        //public：功能惰性 Bind 出新条目后调用本方法使条目缓存失效。

        public static void NoteEntryBound() { _entryVersion++; }

        internal static void ClearEntryCache() {
            _entryCache.Clear();
            _secCache.Clear();
            _secCacheVer.Clear();
            _entryVersion++;
        }

        private static ConfigEntryBase FindInternalEntry(string section, string key) {
            if (_internalConfig == null) return null;
            string k = section + "\t" + key;
            ConfigEntryBase hit;
            if (_entryCache.TryGetValue(k, out hit)) return hit;
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section == section && e.Definition.Key == key) {
                    _entryCache[k] = e;   //只缓存命中
                    return e;
                }
            }
            return null;
        }

        private static string FormatDefaultValue(ConfigEntryBase entry) {
            try {
                if (entry == null || entry.DefaultValue == null) return "";
                object dv = entry.DefaultValue;
                //开关文本须用 UseEn：语言包 FallbackId="English" 时 _langEn 为 false，写死会造成中英混排。
                if (dv is bool) return ((bool)dv) ? (UseEn ? "ON" : "开") : (UseEn ? "OFF" : "关");
                if (dv is float) return ((float)dv).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                if (dv is int) return ((int)dv).ToString();
                //KeyCode 也是 Enum，必须先判；未绑定（None）视为无默认值，不追加默认行。
                if (dv is KeyCode) { KeyCode kc = (KeyCode)dv; return kc == KeyCode.None ? "" : KeyDisplayName(kc); }
                if (dv is System.Enum) return EnumDisplayName(dv.ToString());
                return Convert.ToString(dv, System.Globalization.CultureInfo.InvariantCulture);
            } catch { return ""; }
        }

        //渲染某栏目的通用设置条目（供注册了自绘页但仍需通用设置列表的栏目使用）。
        public static void RenderSectionEntries(string section, System.Func<ConfigEntryBase, bool> filter = null) {
            try {
                float colWidth = EntryNameWidth();
                List<ConfigEntryBase> list = SectionEntries(section);
                for (int i = 0; i < list.Count; i++) {
                    if (filter != null && !filter(list[i])) continue;   //排除属于其它子页的条目
                    RenderEntryRow(list[i], true, colWidth);
                }
            } catch (Exception __ex) { Guard.Log("渲染栏目设置(" + section + ")", __ex); }
        }

        private static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) {
            //条目可能因功能未初始化 / 键改名而取不到，为 null 时直接返回。
            if (entry == null) return;
            string name = isInternal ? ZhKey(entry) : entry.Definition.Key;
            //描述：中文模式取 ZhDesc 或配置描述；英文模式只取英文表。
            string desc;
            if (UseEn) {
                desc = isInternal ? ZhDesc(entry) : null;
                //仅外部条目回退其自带的 ConfigDescription（本身为英文）；内部条目不可回退 Bind 时的中文说明。
                if (desc == null && !isInternal && entry.Description != null) desc = entry.Description.Description;
                if (desc == null) desc = "";
            } else {
                string zhDesc = isInternal ? ZhDesc(entry) : null;
                desc = zhDesc != null ? zhDesc
                    : (entry.Description != null && entry.Description.Description != null ? entry.Description.Description : "");
            }
            //按本行控件的实际占宽从可用空间扣除，供名称列自适应（复选框约 34，键位 / 下拉约 170）。
            float reserve = Sc(180);
            object cval = entry.BoxedValue;
            if (cval is bool) reserve = Sc(34);
            else {
                float hookW = SR.RowComboWidth(entry);
                if (hookW > 0f) reserve = Sc(hookW + 8);
            }
            //标签宽度取本行文字实测宽度，nameW 仅为上限（超出则截断 / 换行），使控件紧贴标签。
            float avail = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(24));
            float textW = NameTextWidth(entry, name);
            float cap = Mathf.Max(Sc(50), avail - reserve);
            float nameW = Mathf.Min(Mathf.Max(colWidth, Sc(50)), cap);
            float nameTw = Mathf.Min(textW, nameW);   //绘制宽度 = 文字宽（夹在上限内）
            //名称列包在垂直组内才能获得完整高度，否则 CJK 字形下沉 / 换行会被裁。
            //Begin/End 必须成对：中途抛异常而未闭合分组会让后续控件全部落错分组。
            GUILayout.BeginHorizontal();
            try {
                GUILayout.BeginVertical(GUILayout.Width(nameTw));
                try {
                    //整串走 RowTip（按 entry 缓存，语言或条目变化时由 NameCacheSync 清空）。
                    string tip = RowTip(entry, desc);
                    if (GUILayout.Button(new GUIContent(name, tip),
                        _nameLabel, GUILayout.Width(nameTw), GUILayout.Height(TextHeight(name, nameTw)))) {
                        //只认左键：右键用于绑定快捷键，不可同时重置默认值
                        Event evName = Event.current;
                        if (evName == null || evName.button == 0) {
                            try { entry.BoxedValue = entry.DefaultValue; } catch (Exception __ex) { Guard.Log("恢复条目默认值(点击名称)", __ex); }
                            //复位可能改动键位，须使序列监听的 watch 列表失效。
                            MarkKeyChanged();   //重置可能改动键位，令序列监听列表失效
                            _editText.Remove(entry);
                            _editOpen.Remove(entry);
                        }
                    }
                } finally { GUILayout.EndVertical(); }
                //排版约定：控件紧贴其标签右侧。名称标签宽度即 nameTw，其后不插 FlexibleSpace，
                //也不做主控件列对齐补白，避免在控件与标签之间留白。
                GUILayout.Space(Sc(6));
                RenderControl(entry);
                RenderFollowUp(entry, isInternal);
            } finally { GUILayout.EndHorizontal(); }
            GUILayout.Space(Sc(2));
        }

        //绘制同一行的同伴键位与附加控件。
        //滑块路径会先 EndHorizontal 再 return，故它须在自己的水平组内调用本方法。
        private static void RenderFollowUp(ConfigEntryBase entry, bool isInternal) {
            try {
                //滑块路径与外层 RenderEntryRow 都会调用本方法，同一条目同帧只渲染一次。
                if (_followDoneEntry == entry && _followDoneFrame == Time.frameCount) return;
                _followDoneEntry = entry; _followDoneFrame = Time.frameCount;
                string companionKey = isInternal ? SR.RowCompanion(entry) : null;
                if (companionKey != null) {
                    GUILayout.Space(Sc(2));
                    ConfigEntryBase ck = FindInternalEntry(entry.Definition.Section, companionKey);
                    if (ck != null) RenderControl(ck);
                }
                if (!isInternal) return;
                string extraKey = SR.RowExtra(entry);
                if (extraKey == null) return;
                GUILayout.Space(Sc(2));
                ConfigEntryBase ex = FindInternalEntry(entry.Definition.Section, extraKey);
                if (ex != null) RenderControl(ex);
            } catch (Exception __ex) { Guard.Log("同行附加控件", __ex); }
        }

        //文本高度 + CJK 字形下沉余量，避免标签裁剪；缓存为两层字典（文本 -> [宽度, 缩放, 高度]）。
        private static float TextHeight(string text, float width) {
            try {
                List<float[]> list;
                if (!_heightCache.TryGetValue(text, out list)) { list = new List<float[]>(2); _heightCache[text] = list; }
                for (int i = 0; i < list.Count; i++) {
                    float[] e = list[i];
                    if (Mathf.Approximately(e[0], width) && Mathf.Approximately(e[1], _scaled)) return e[2];
                }
                float v = Mathf.Max(Sc(32), _nameLabel.CalcHeight(new GUIContent(text), width) + Sc(16));
                if (_heightCache.Count > 800) _heightCache.Clear(); //防膨胀
                list.Add(new[] { width, _scaled, v });
                return v;
            } catch {
                return Sc(32);
            }
        }

        //Dear ImGui 风格水平滑块：轨道 + 填充段 + 圆形把手（三态）；左键拖动或点击轨道跳转，悬停滚轮微调。
        //输入与热区交给 GUI.HorizontalSlider（由引擎维护 hotControl），外观自绘；返回值与传入值不同即提交。

        //原生 slider 用的两个不可见样式：只取其输入与热区，外观自绘。
        private static GUIStyle _sliderNative;
        private static GUIStyle _sliderNativeThumb;
        //上次提交给配置的值（抑制未变化时的重复写盘）
        private static readonly System.Collections.Generic.Dictionary<ConfigEntryBase, float> _sliderLastWritten =
            new System.Collections.Generic.Dictionary<ConfigEntryBase, float>();

        //帧末清理：切场景 / 复位时清掉"展开当帧不绘制"的记录。
        internal static void SliderFrameEndCleanup() {
            _comboSkipFrame.Clear();
        }

        private static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false, float step = 0f) {
            float trackH = Sc(4), handleD = Sc(16);
            float trackW = Mathf.Max(1f, rect.width - handleD);

            //输入层：不可见的原生 HorizontalSlider，只吃事件与热区，跨趟拖动稳定。
            if (_sliderNative == null || _sliderNativeThumb == null) {
                _sliderNative = new GUIStyle();
                _sliderNativeThumb = new GUIStyle();
                _sliderNative.normal.background = null;
                _sliderNativeThumb.normal.background = null;
            }
            float raw = GUI.HorizontalSlider(rect, value, min, max, _sliderNative, _sliderNativeThumb);

            //按调用方给的 step 量化；step<=0 时才按整数取整（大跨度下取整会表现为拖不动）。
            float nv = raw;
            if (step > 0f) nv = Mathf.Round(nv / step) * step;
            else if (intStep) nv = Mathf.Round(nv);
            nv = Mathf.Clamp(nv, min, max);

            //轨道两端各留半个把手，使最小 / 最大时把手完整落在 rect 内
            float t = Mathf.Clamp01(Mathf.InverseLerp(min, max, nv));
            Rect trackRect = new Rect(rect.x + handleD / 2f, rect.y + (rect.height - trackH) / 2f, trackW, trackH);
            Rect fillRect = new Rect(trackRect.x, trackRect.y, trackW * t, trackH);
            Rect handleRect = new Rect(rect.x + trackW * t, rect.y + (rect.height - handleD) / 2f, handleD, handleD);
            GUI.Box(trackRect, GUIContent.none, _sliderTrack);
            if (fillRect.width > 0.5f) GUI.Box(fillRect, GUIContent.none, _sliderFill);
            //把手三态：拖动中（原生 slider 持有 hotControl）/ 悬停 / 常态
            bool dragging = GUIUtility.hotControl != 0 && rect.Contains(Event.current.mousePosition);
            bool hover = rect.Contains(Event.current.mousePosition);
            GUIStyle hs = dragging ? _sliderHandleActive : (hover ? _sliderHandleHover : _sliderHandle);
            GUI.Box(handleRect, GUIContent.none, hs);
            GUI.DrawTexture(new Rect(handleRect.x + handleD / 2f - Sc(2), handleRect.y + handleD / 2f - Sc(2), Sc(4), Sc(4)), Texture2D.whiteTexture);
            return nv;
        }

        //提交入口：值不同于上次写入值时才写配置。
        private static bool SliderSubmit(ConfigEntryBase entry, float value) {
            if (entry == null) return false;
            float last;
            if (_sliderLastWritten.TryGetValue(entry, out last) && Mathf.Abs(last - value) < 0.0001f) return false;
            _sliderLastWritten[entry] = value;
            SetValue(entry, value);
            return true;
        }

        //设置页各数值条目的滑块范围（唯一真值取自 Ui），经"行滑块"扩展点提供。
        private static bool SettingsSliderRange(ConfigEntryBase entry, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0.0";
            if (entry == null || entry.Definition.Section != Keys.Settings) return false;
            string key = entry.Definition.Key;
            if (key == Keys.UiScale) { min = Ui.ScaleMin; max = Ui.ScaleMax; fmt = "0.0"; return true; }
            if (key == Keys.WinWidth) { min = Ui.WinWMin; max = Ui.WinWMax; fmt = "0"; return true; }
            if (key == Keys.WinHeight) { min = Ui.WinHMin; max = Ui.WinHMax; fmt = "0"; return true; }
            if (key == Keys.WinX) { min = Ui.PosMin; max = Ui.PosMax; fmt = "0"; return true; }
            if (key == Keys.WinY) { min = Ui.PosMin; max = Ui.PosMax; fmt = "0"; return true; }
            if (key == Keys.SidebarWidth) { min = Ui.SidebarWMin; max = Ui.SidebarWMax; fmt = "0"; return true; } //0 = 自动
            return false;
        }

        //界面语言下拉候选 = 内置中文 / English + 已注册语言包，经"行下拉候选"扩展点提供。
        private static bool SettingsComboOptions(ConfigEntryBase entry, out string[] values, out string[] displayNames) {
            values = null; displayNames = null;
            if (entry == null || entry.Definition.Section != Keys.Settings || entry.Definition.Key != Keys.Language) return false;
            SR.LanguageOptions(out values, out displayNames);
            return values != null && displayNames != null;
        }

        //KeyboardShortcut 的显示格式：修饰键在前（如 "Ctrl + F"），与 SR 键位框一致。
        private static string KbShortcutText(BepInEx.Configuration.KeyboardShortcut ks) {
            try {
                if (ks.MainKey == KeyCode.None) return "-";
                string s = "";
            string __m = "";
            try { __m = ks.Modifiers.ToString(); } catch { __m = ""; }
            if (__m.IndexOf("Control", System.StringComparison.OrdinalIgnoreCase) >= 0) s += "Ctrl + ";
            if (__m.IndexOf("Shift", System.StringComparison.OrdinalIgnoreCase) >= 0) s += "Shift + ";
            if (__m.IndexOf("Alt", System.StringComparison.OrdinalIgnoreCase) >= 0) s += "Alt + ";
                return s + KeyDisplayName(ks.MainKey);
            } catch { return ks.ToString(); }
        }

        private static void RenderControl(ConfigEntryBase entry) { RenderControl(entry, true); }

        //带 isInternal 的重载：滑块路径据此决定是否绘制同行附加控件。
        private static void RenderControl(ConfigEntryBase entry, bool isInternal) {
            _curIsInternal = isInternal;
            object val = entry.BoxedValue;
            if (val is bool) {
                //复选框只负责开关（左键切换），快捷键绑定走其左侧标签（见 RestoreLabel / RenderEntryRow）
                bool b = (bool)val;
                if (GUILayout.Button(b && _checkTex == null ? "✓" : "", b ? _checkOn : _checkOff, GUILayout.Width(CtlH()), GUILayout.Height(Sc(26)))) {
                    SetValue(entry, !b);
                }
                //勾贴图叠画在复选框上（底色仍为淡蓝），贴图缺失时回退字形
                if (b && _checkTex != null) {
                    Rect __ckr = GUILayoutUtility.GetLastRect();
                    float __ckm = __ckr.width * 0.18f;
                    GUI.DrawTexture(new Rect(__ckr.x + __ckm, __ckr.y + __ckm, __ckr.width - __ckm * 2f, __ckr.height - __ckm * 2f), _checkTex);
                }
            } else if (val is KeyCode) {
                bool capturing = _capturing == entry;
                string text;
                if (capturing) {
                    text = RecText();
                } else {
                    KeyCode kc = (KeyCode)val;
                    //组合键显示：Shift/Ctrl/Alt + 主键
                    text = ComboKeyDisplay(entry, kc);
                }
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(CtlH()))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; _recSeq.Clear(); _recHeld.Clear(); }
                }
                //键位框同样支持右键绑定 / 解绑
                ConfigEntry<KeyCode> kcRow = entry as ConfigEntry<KeyCode>;
                if (kcRow != null) TryBindByRightClick(kcRow, GUILayoutUtility.GetLastRect());
            } else if (val is BepInEx.Configuration.KeyboardShortcut) {
                //外部模组（如 BetterFreeplay）的组合键用 KeyboardShortcut
                bool capturing = _capturing == entry;
                //外部插件的 KeyboardShortcut 同样按"修饰键在前"格式显示
                string text = capturing ? RecText() : KbShortcutText((BepInEx.Configuration.KeyboardShortcut)val);
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(CtlH()))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; _recSeq.Clear(); _recHeld.Clear(); }
                }
            } else if (val is Enum) {
                Type et = val.GetType();
                string cur = EnumDisplayName(val.ToString());
                //枚举选项按 entry 缓存（值不变，显示名每帧现算）
                object[] cachedVals;
                if (!_enumOptions.TryGetValue(entry, out cachedVals)) {
                    string[] names = Enum.GetNames(et);
                    Array vals = Enum.GetValues(et);
                    List<object> vlist = new List<object>();
                    for (int i = 0; i < names.Length; i++) {
                        object v = vals.GetValue(i);
                        //功能可注册枚举项过滤（如关卡下拉框排除 空白 / 随机 / 原型）
                        if (!SR.RowEnumAllowed(entry, names[i], Convert.ToInt32(v))) continue;
                        vlist.Add(v);
                    }
                    cachedVals = vlist.ToArray();
                    _enumOptions[entry] = cachedVals;
                }
                string[] dispNames = new string[cachedVals.Length];
                for (int i = 0; i < cachedVals.Length; i++) dispNames[i] = EnumDisplayName(cachedVals[i].ToString());
                bool open;
                if (!_editOpen.TryGetValue(entry, out open)) open = false;
                //下拉框宽度：默认 170，功能可注册更窄的宽度
                float hookW = SR.RowComboWidth(entry);
                float cbw = hookW > 0f ? Sc(hookW) : Sc(170);
                int sel = ComboBox(entry, cur, cachedVals, dispNames, ref open, cbw);
                if (sel >= 0) SetValue(entry, cachedVals[sel]);
                _editOpen[entry] = open;
            } else if (val is int || val is float || val is string) {
                //功能可注册该数值条目用滑块渲染；拖动不会带动窗口（滑块已取得 hot control）
                float hmin, hmax;
                string hfmt;
                if ((val is float || val is int) && SR.RowSliderRange(entry, out hmin, out hmax, out hfmt)) {
                    float fv = val is int ? (int)val : (float)val;
                    float nv = fv;
                    //跨度大时量化步长取 跨度/60，使整条轨道至少约 60 个可停点；float 条目不量化。
                    float hstep = 0f;
                    if (val is int) { float span = hmax - hmin; hstep = span > 4000f ? span / 60f : 1f; }
                    Rect sr = default(Rect);
                    GUILayout.BeginHorizontal();
                    try {
                        sr = GUILayoutUtility.GetRect(Sc(150), CtlH(), GUILayout.Width(SliderTrackWidth()));
                        nv = DrawSlider(sr, fv, hmin, hmax, val is int, hstep); //int 条目按整数步进，提交时由 SetValue 归一化类型
                        //float 条目可选步进（范围由 SRApi.RowSliders 给出，粒度由 RowSliderSteps 给出）
                        float fstep;
                        if (RowSliderStep(entry, out fstep))
                            nv = Mathf.Clamp(hmin + Mathf.Round((nv - hmin) / fstep) * fstep, hmin, hmax);
                        string __sv = nv.ToString(hfmt);
                        GUILayout.Label(__sv, _label, GUILayout.Width(SliderValueWidth(__sv)), GUILayout.Height(Sc(26)));
                        //附加控件（快捷键框）画在同一水平组内并紧跟数值标签，否则会被拆到别的行去。
                        RenderFollowUp(entry, _curIsInternal);
                    } finally { GUILayout.EndHorizontal(); }
                    //值变化即写配置
                    SliderSubmit(entry, nv);
                    return;
                }
                //字符串条目：注册了下拉候选时用下拉框渲染。
                //候选由功能注册（界面语言见 SettingsComboOptions，内置中文 / English + 已注册语言包），内核不认识具体键。
                string[] cvals, cnames;
                if (val is string && SR.RowComboOptionList(entry, out cvals, out cnames)) {
                    bool open;
                    if (!_editOpen.TryGetValue(entry, out open)) open = false;
                    string cur = (string)val;
                    int curIdx = Array.IndexOf(cvals, cur);
                    if (curIdx < 0) curIdx = Array.IndexOf(cvals, LangEn); //语言包已删除时显示回退结果
                    if (curIdx < 0) curIdx = 0;
                    int sel = ComboBox(entry, cnames[curIdx], cvals, cnames, ref open, Sc(120));
                    //cvals 即落盘值（如语言 id）
                    if (sel >= 0 && sel < cvals.Length) SetValue(entry, cvals[sel]);
                    _editOpen[entry] = open;
                    return;
                }
                string txt;
                if (!_editText.TryGetValue(entry, out txt)) {
                    txt = Convert.ToString(val, CultureInfo.InvariantCulture);
                    _editText[entry] = txt;
                }
                //编辑框宽度：默认 170，功能可注册更窄的宽度
                float hookEditW = SR.RowComboWidth(entry);
                float editW = hookEditW > 0f ? Sc(hookEditW) : Sc(170);
                string ntxt = ClearableTextField(txt, _searchBox, GUILayout.Width(editW), GUILayout.Height(Sc(26)));
                if (ntxt != txt) {
                    _editText[entry] = ntxt;
                    _editSelfFrame = Time.frameCount;   //标记改动由编辑框自身发起
                    if (val is int) {
                        int iv;
                        if (int.TryParse(ntxt, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv)) SetValue(entry, iv);
                    } else if (val is float) {
                        float fv;
                        if (float.TryParse(ntxt, NumberStyles.Float, CultureInfo.InvariantCulture, out fv)) SetValue(entry, fv);
                    } else {
                        SetValue(entry, ntxt);
                    }
                }
            } else {
                GUILayout.Label(Convert.ToString(val), _label, GUILayout.Width(Sc(170)));
            }
        }

        //下拉框（全 mod 唯一实现）：按钮与展开列表同属一个纵向组，列表落在按钮正下方并对齐。
        //展开当帧不绘制列表（当帧新建控件无 Layout 记录），跳过帧号按条目记录；width 传入前已 Sc() 过。
        private static readonly Dictionary<ConfigEntryBase, int> _comboSkipFrame = new Dictionary<ConfigEntryBase, int>();

        //展开列表的滚动起点（按条目记录，最多可见 8 项）。
        private static readonly Dictionary<ConfigEntryBase, int> _comboScroll = new Dictionary<ConfigEntryBase, int>();
        //编辑框自身提交配置的帧号（该帧不清 _editText，避免吃掉小数点）
        private static int _editSelfFrame = -1;
        //下拉框"点外面关闭"：不靠坐标（下拉框矩形在滚动区内容坐标系里），而是 MouseDown 这一帧若无下拉框认领
        //（ComboMouseDownSeen），则在 ManagerUI.OnGUI 末尾全部收起。
        internal static int _comboSeenFrame = -1;
        internal static void ComboMouseDownSeen() { _comboSeenFrame = Time.frameCount; }
        internal static void CloseOpenCombos() {
            try {
                //先拷贝键再改值：边枚举边写会抛 "Collection was modified" 异常。
                ConfigEntryBase[] keys = new ConfigEntryBase[_editOpen.Count];
                _editOpen.Keys.CopyTo(keys, 0);
                for (int i = 0; i < keys.Length; i++) if (_editOpen[keys[i]]) _editOpen[keys[i]] = false;
            } catch (Exception __ex) { Guard.Log("收起展开的下拉框", __ex); }
        }

        // 滚轮所有权：控件在被 hover 时声明独占；帧首把本帧声明转存为上一帧声明，滚动仲裁读上一帧声明，
        // 有则整体放弃滚动（滚动容器的处理发生在控件提交之前，故只能读上一帧）。
        private static bool _wheelClaimCur;
        private static bool _wheelClaimPrev;
        private static int _wheelClaimFrame = -1;

        private static float SliderValueWidth(string text) {
            try {
                return Mathf.Max(Sc(44), _label.CalcSize(new GUIContent(text)).x + Sc(8));
            } catch { return Sc(44); }
        }
        // 中心化 hover 裁决（借鉴 ItemHoverable）：一帧内只有第一个判定通过的控件算 hovered，叠放控件须显式 AllowOverlap。
        private static int _hoverFrame = -1;
        private static bool _hoverTaken;
        //本控件是否赢得本帧 hover；allowOverlap = true 时无视先到先得（叠放控件用）
        internal static bool HoverTest(Rect r, bool allowOverlap = false) {
            try {
                Event e = Event.current;
                if (e == null) return false;
                if (_hoverFrame != Time.frameCount) { _hoverFrame = Time.frameCount; _hoverTaken = false; }
                if (!r.Contains(e.mousePosition)) return false;
                if (!allowOverlap && _hoverTaken) return false;   //先到先得
                _hoverTaken = true;
                return true;
            } catch { return false; }
        }

        internal static void WheelClaimNewFrame() {
            try {
                if (_wheelClaimFrame == Time.frameCount) return;
                _wheelClaimFrame = Time.frameCount;
                _wheelClaimPrev = _wheelClaimCur;
                _wheelClaimCur = false;
            } catch (Exception __ex) { Guard.Log("WheelClaimNewFrame", __ex); }
        }
        public static void WheelClaim() { try { _wheelClaimCur = true; } catch (Exception __ex) { Guard.Log("WheelClaim", __ex); } }
        internal static bool WheelClaimedPrev { get { try { return _wheelClaimPrev; } catch { return false; } } }
        //控件高度（设计像素）：0 = 默认 26；功能可覆盖自己页面的控件高度
        public static float ControlHeight;
        //内容区原点偏移，供事件坐标与内容坐标换算
        internal static Vector2 AreaOffset;
        internal static bool WheelShieldHit;
        //本帧待用的滚轮量：滚动区在 ScrollWheel 趟经 WheelDeltaSet 记下，控件经 WheelDeltaTake 取用（取用即清）。
        private static float _wheelDelta;
        internal static void WheelDeltaSet(float d) { try { _wheelDelta = d; } catch (Exception __ex) { Guard.Log("WheelDeltaSet", __ex); } }
        internal static float WheelDeltaTake() { try { float d = _wheelDelta; _wheelDelta = 0f; return d; } catch { return 0f; } }
        internal static void ResetWheelState() {
            try {
                _wheelClaimPrev = false; _wheelClaimCur = false; _wheelClaimFrame = -1;
                WheelShieldHit = false; _wheelDelta = 0f;
                _sliderLastWritten.Clear();   //跨场景后配置可能已变，清空已写入值表
            } catch (Exception __ex) { Guard.Log("ResetWheelState", __ex); }
        }

        //控件高度：默认 26，功能可用 SR.Ctl.ControlHeight 覆盖
        private static float CtlH() { try { return Sc(ControlHeight > 0f ? ControlHeight : 26f); } catch { return Sc(26); } }

        // 滑块轨道的固定宽度（像素）：GetRect 不带 ExpandWidth 时宽度会被父级水平布局按剩余空间分配，
        // 须显式 GUILayout.Width 锁定；仍经 Sc() 随 UI 缩放，但不随窗口宽度变化。
        public static float SliderTrackWidth() { try { return Sc(150); } catch { return 150f; } }

        private static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = Sc(170);
            int clicked = -1;
            //"本帧是否展开"按进入时的状态决定，保证 Layout 与 Repaint 的控件结构一致
            int skipFrame;
            bool drew = open && !(entry != null && _comboSkipFrame.TryGetValue(entry, out skipFrame) && skipFrame == Time.frameCount);
            GUILayout.BeginVertical(GUILayout.Width(width));
            if (GUILayout.Button(current + "   v", _frame, GUILayout.Width(width), GUILayout.Height(CtlH()))) {
                ComboMouseDownSeen();   //本下拉框被点击
                open = !open;
                if (entry != null) _editOpen[entry] = open;   //null 保护：字典不接受 null 键
                if (entry != null) _comboSkipFrame[entry] = Time.frameCount; //展开当帧不绘制列表
            }
            //悬停滚轮换选项：在 Repaint 阶段落值，此时改配置不会打断本帧 Layout（下拉框宽度固定）。
            Rect boxRect = GUILayoutUtility.GetLastRect();
            //控件在被 hover 时声明滚轮所有权
            Event ce0 = Event.current;
            if (ce0 != null && options != null) {
                Rect listRect = new Rect(boxRect.x, boxRect.yMax, width, Sc(26) * options.Length);
                //框命中即赢；展开的列表用 AllowOverlap 抢下 hover（它叠放在下方各行之上）
                bool won = HoverTest(boxRect) || (drew && HoverTest(listRect, true));
                if (won) WheelClaim();   //仅赢得 hover 时声明，一帧内不会有两个控件同时声明
            }
            // 滚轮换选项仅在收起时生效：展开时滚轮留给列表与页面滚动。
            Event ce = Event.current;
            if (!open && ce != null && ce.type == EventType.Repaint && options != null && options.Length > 1
                && boxRect.Contains(ce.mousePosition)) {
                //取滚轮盾在事件中记下的量：本环境下 Input.GetAxis("Mouse ScrollWheel") 恒为 0。
                float cw = WheelDeltaTake();
                if (Mathf.Abs(cw) > 0.0001f) {
                    int cur = 0;
                    for (int i = 0; i < options.Length; i++) { if (options[i] == current) { cur = i; break; } }
                    int ni = cw > 0f ? cur - 1 : cur + 1; //上滚为上一项
                    if (ni < 0) ni = options.Length - 1;
                    if (ni >= options.Length) ni = 0;
                    clicked = ni;
                    SR.MarkWheelUsed();   //标记滚轮已消费，地图 / 自由相机不再缩放
                }
            }
            //展开状态下滚轮滚列表；WheelClaim 已登记，滚动区不会抢占。
            if (open && drew && ce != null && ce.type == EventType.Repaint && options != null && options.Length > 8
                && entry != null) {
                Rect __lr = new Rect(boxRect.x, boxRect.yMax, width, CtlH() * 8f);
                if (__lr.Contains(ce.mousePosition)) {
                    float __wv = WheelDeltaTake();
                    if (Mathf.Abs(__wv) > 0.0001f) {
                        int __sc;
                        if (!_comboScroll.TryGetValue(entry, out __sc)) __sc = 0;
                        __sc = Mathf.Clamp(__sc + (__wv > 0f ? -2 : 2), 0, options.Length - 8);
                        _comboScroll[entry] = __sc;
                        SR.MarkWheelUsed();
                    }
                }
            }
            if (drew) {
                //展开列表最多可见 8 项，其余经滚轮滚动。
                int __visN = Mathf.Min(options.Length, 8);
                int __first = 0;
                if (options.Length > __visN && entry != null) {
                    int __cur = -1;
                    for (int i = 0; i < options.Length; i++) { if (options[i] == current) { __cur = i; break; } }
                    int __sc;
                    if (!_comboScroll.TryGetValue(entry, out __sc)) __sc = 0;
                    if (__cur >= 0) {
                        if (__cur < __sc) __sc = __cur;                              //选中项在窗口上方：跟上去
                        else if (__cur >= __sc + __visN) __sc = __cur - __visN + 1;  //在下方：贴底
                    }
                    __sc = Mathf.Clamp(__sc, 0, options.Length - __visN);
                    _comboScroll[entry] = __sc;
                    __first = __sc;
                }
                int __last = Mathf.Min(options.Length, __first + __visN);
                for (int i = __first; i < __last; i++) {
                    bool isSel = options[i] == current;
                    if (GUILayout.Button(options[i], isSel ? _selItem : _item, GUILayout.Width(width), GUILayout.Height(CtlH()))) clicked = i;
                }
            }
            GUILayout.EndVertical();
            if (clicked >= 0) {
                ComboMouseDownSeen();   //列表项被点击
                open = false;
                //null 保护：调用方可能传 null（FindEntry 未命中），字典不接受 null 键。
                if (entry != null) _editOpen[entry] = false;
            }
            return clicked; //调用方按索引取 value 并落配置
        }

    }
}
