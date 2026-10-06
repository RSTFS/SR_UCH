// 设置页。设置页本体和通用条目行的渲染、滑块、下拉框、侧栏自定义顺序，
// 还有给 Configuration Manager 的排序 / 隐藏，以及滚轮屏蔽。
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

        // 让 Configuration Manager 的条目顺序与 SR 界面一致
        //Configuration Manager 按条目 Tag 里的 ConfigurationManagerAttributes.Order 排序；
        //BepInEx 的 ConfigDescription / ConfigEntryBase.Description 都没有公开 setter，所以这里用反射替换
        //Description（只改显示顺序的标签，Value/AcceptableValues 原样保留）。
        //顺序规则 = 侧栏栏目顺序（_internalSections）-> 栏目内条目的注册（绑定）顺序，与 SR 页面渲染顺序一致。
        private static int _cmOrderVer = -1;   //上次应用排序时的条目版本（惰性 Bind 出新条目要重跑）
        private static readonly FieldInfo _fEntryDesc =
            typeof(ConfigEntryBase).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(ConfigEntryBase).GetField("Description", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void ApplyCmOrder() {
            if (_internalConfig == null) return;
            if (_cmOrderVer == _entryVersion) return;   //条目表没变：直接返回
            _cmOrderVer = _entryVersion;
            try {
                //Configuration Manager 是 OrderByDescending(Order)：**Order 大的显示在前面**（反查它 IL 确认的）
                //-> 每个栏目内把顺序倒过来编：先绑定的条目拿最大的 Order。
                Dictionary<string, int> secRank = new Dictionary<string, int>();
                //OrderByDescending：Order 大的在前。侧栏第一个栏目应最前 -> rank 取总数-1-i；
                //原来取 i（第一个栏目 rank=0=最小）会整列倒过来。
                for (int i = 0; i < _internalSections.Count; i++) secRank[_internalSections[i]] = _internalSections.Count - 1 - i;
                //先按栏目分组收集，拿到每栏条目数才能倒着编号
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
                    int r; if (!secRank.TryGetValue(kv.Key, out r)) r = -1; //没注册成栏目的段（Hotkeys/Reflection 等）Order 最小 = 排最后
                    List<ConfigEntryBase> list = kv.Value;
                    for (int k = 0; k < list.Count; k++) {
                        ConfigEntryBase e = list[k];
                        int order = r * 1000 + (list.Count - 1 - k); //倒序编号：第 1 个绑定的排最前
                        try {
                            ConfigDescription d = e.Description;
                            if (_fEntryDesc != null) {
                                //Browsable=false + HideSetting=true：SR 有自己的界面，不需要 CM 再列一遍，
                                //整个 SR_UCH 插件因此在 CM 的条目列表里不出现（SR 仍由 BepInEx 正常加载）。
                                object[] tags = new object[] { new ConfigurationManagerAttributes { Order = order, Browsable = false, HideSetting = true } };
                                _fEntryDesc.SetValue(e, new ConfigDescription(d != null ? d.Description : "", d != null ? d.AcceptableValues : null, tags));
                            }
                        } catch (Exception __ex) { Guard.Log("设置条目显示顺序(单条)", __ex); }
                    }
                }
            } catch (Exception __ex) { Guard.Log("Configuration Manager 排序", __ex); }
        }

        //枚举下拉选项缓存（值不变；显示名按语言每帧现算）, 避免每帧 GetNames/GetValues/遍历
        //枚举选项缓存：键是 **entry 而非 Type**, 过滤器 RowEnumAllowed 的签名带 entry
        //（同一枚举类型被不同配置项使用时过滤结果可能不同），按 Type 缓存会让先渲染的 entry
        //的过滤结果被后续同类型 entry 复用（表现为下拉框选项串台，且取决于渲染顺序）。
        private static readonly Dictionary<ConfigEntryBase, object[]> _enumOptions = new Dictionary<ConfigEntryBase, object[]>();

        private static void SetValue(ConfigEntryBase entry, object value) {
            try {
                //类型归一化：滑块/输入框给的是 float，而 int 条目只接受 int（否则 BepInEx 抛异常 -> 表现为"怎么滑都调不动"）
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
                // **R411**：外部改值时同步编辑框（否则文本框继续显示旧值，直到切页才恢复）。
                //  不能无条件清：编辑框自己提交时（打 "1." -> 值 1.0 -> 清文本 -> 下一帧回显 "1"）
                //  会把小数点吃掉，所以只在"这次改动不是编辑框自己发起"时才清。
                if (_editSelfFrame != Time.frameCount) _editText.Remove(entry);
            } catch (Exception ex) {
                SR.LogWarn("配置修改失败: " + ex.Message);
            }
        }

        private static List<ConfigEntryBase> InternalSectionEntries() {
            //性能：原来是"每帧 new List + 全表遍历"，而 SectionEntries 早就有按 _entryVersion 失效的缓存
            //（同一套过滤：段名 + RowVisible）-> 直接复用，栏目切换时各段各自缓存。
            return SectionEntries(_selectedInternalSection);
        }

        //设置页条目按类别分组（无关项之间留视觉间隔）；顺序与 config 绑定顺序一致，插件组最后
        private static readonly string[][] _settingsGroups = new string[][] {
            new[] { "按键", "Open Key", "Block Input" },
            new[] { "界面", "UI Scale", "Window Width", "Window Height", "Window X", "Window Y", "Language" },
            new[] { "插件", "Disabled Plugins" },
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
                //栏目宽度专用行：滑块 + [-]/[+] 步进（滑块失效时步进按钮仍可靠）
                if (entry.Definition.Key == "Sidebar Width") { RenderSidebarWidthRow(); continue; }
                string g = SettingsGroup(entry.Definition.Key);
                //外部插件清单挪到界面分组之后（原来在最上面，把界面设置挤下去了）
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
            RenderAuditBlock();   //地基可见性：设置页底部的自检块
        }

        //调试信息块：只有 SR 调试模式打开时才出现（EX 高级页可切换）。
        //内容全部由 SR.DebugLines() 生成, UI 只负责画，逻辑留在核心，便于日志/外部复用。
        
        //自检块：把启动自检的结果显示出来（补丁目标 + 反射成员）。
        //为什么放在设置页：它是诊断信息，不该占侧栏栏目、也不该每次开面板都抢视线。
        private static void RenderAuditBlock() {
            try {
                GUILayout.Space(Sc(6));
                GUILayout.Label("- " + T("自检", "Self-check") + " -", _secHeader);
                //两份自检的可用数
                GUILayout.Label(T("按名绑定的补丁目标", "Named patch targets") + "："
                    + SR.AuditPatchOk + " / " + SR.AuditPatchTotal, _label);
                GUILayout.Label(T("反射成员", "Reflected members") + "：" + SR.AuditRefOk + " / " + SR.AuditRefTotal, _label);
                //失效清单
                System.Collections.Generic.IList<SR.AuditIssue> issues = SR.AuditIssues;
                if (issues == null || issues.Count == 0) {
                    GUILayout.Label(T("按名绑定的目标全部有效。", "All name-bound targets are valid."), _label);
                } else {
                    GUILayout.Label(T("以下目标已失效 -> 对应功能不会生效（开关还在、行为没了）：",
                        "These targets no longer exist - the listed features will NOT work:"), _label);
                    for (int i = 0; i < issues.Count; i++) {
                        GUILayout.Label("   ✗  " + issues[i].Kind + "  " + issues[i].Target + "   <- " + issues[i].Scope, _label);
                    }
                }
                // **运行时健康度**：Guard 的 WarnCount / LastWhat / LastError 本来就是为"离线自检 / 问题上报"
                //  准备的（见 Guard 里那段注释），但**一直没有任何地方读它们**。
                //  启动自检看的是"静态失效"（成员/补丁名没了），运行期异常则完全不可见,
                //  玩家遇到"点了没反应"时，日志里可能只有一条被 5 秒去重吞掉的 Warning。
                //  显示出来玩家不用翻日志就能告诉我们发生了什么。
                if (SR.Guard.WarnCount > 0) {
                    GUILayout.Label(T("运行时异常次数", "Runtime errors") + "：" + SR.Guard.WarnCount, _label);
                    if (!string.IsNullOrEmpty(SR.Guard.LastWhat)) {
                        GUILayout.Label(T("最近一次", "Last") + "：" + SR.Guard.LastWhat
                            + " - " + (SR.Guard.LastError ?? ""), _label);
                    }
                }
            } catch (Exception __ex) { Guard.Log("渲染自检块", __ex); }
        }

        private static void RenderSidebarWidthRow() {
            int cur = _sidebarWEntry != null ? Mathf.Clamp(_sidebarWEntry.Value, 0, 320) : 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(T("栏目宽度", "Sidebar Width"), T("左侧栏目栏宽度（0 = 自动，100-320）", "Left sidebar width (0 = auto, 100-320)")), _label, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(26)));
            if (GUILayout.Button("-", _btn, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) SetSidebarWidth(cur - 16);
            Rect sr = GUILayoutUtility.GetRect(Sc(150), CtlH(), GUILayout.Width(SliderTrackWidth()));
            float nv = DrawSlider(sr, cur, 0f, 320f, true);
            // **R416**：值变了就写（原来靠"帧号+几何"认领提交信号，见 DrawSlider 处说明）
            if (Mathf.Abs(nv - cur) > 0.0001f) SetSidebarWidth(Mathf.RoundToInt(nv));
            if (GUILayout.Button("+", _btn, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) SetSidebarWidth(cur + 16);
            GUILayout.Label(cur + " px", _label, GUILayout.Width(Sc(70)), GUILayout.Height(Sc(26)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        //同时更新即时值与配置项（布局立即生效 + 持久化）
        private static void SetSidebarWidth(int px) {
            px = Mathf.Clamp(px, 0, 320);
            _sidebarW = px;
            if (_sidebarWEntry != null) _sidebarWEntry.Value = px;
        }

        //设置页条目：原来每帧 new List + 全表遍历（设置页条目最多）。与 SectionEntries 同一套版本失效缓存，
        //只是多一条组合键 XXX 内部条目不显示的过滤，所以单独缓存一份（而不是塞进 SectionEntries）。
        private static List<ConfigEntryBase> _settingsCache;
        private static int _settingsCacheVer = -1;
        private static List<ConfigEntryBase> SettingsEntries() {
            if (_settingsCache != null && _settingsCacheVer == _entryVersion) return _settingsCache;
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            if (_internalConfig != null) {
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    if (e.Definition.Section != "Settings") continue;
                    //这些项在会话页/搜索栏已有专用开关，设置页不再重复渲染（是否显示由功能自己注册，见 SR.RowFilters）
                    if (!SR.RowVisible(e)) continue;
                    //组合键 XXX是修饰键的内部持久化条目（RegisterComboEntry 建立），不显示在设置页
                    if (e.Definition.Key.StartsWith("组合键 ")) continue;
                    if (e.Definition.Key == "Sidebar Order") continue; //侧栏顺序改为长按拖动，不在设置页露出
                    result.Add(e);
                }
            }
            _settingsCache = result;
            _settingsCacheVer = _entryVersion;
            return result;
        }

        //可点击的条目标签：左键恢复默认值、右键绑定快捷键（开关类自动接入自动快捷键）。
        //非开关条目的默认值显示在悬浮提示里（不占标签文字）。
        //hotkey 传 null 时：bool 条目自动取"row:段.键"的自动快捷键，其余类型不参与。
        private static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h, ConfigEntry<KeyCode> hotkey = null) {
            GUIContent c2 = content;
            ConfigEntry<KeyCode> hk = hotkey; //只认功能显式传入的键（复选框标签不再自动接快捷键）
            try {
                string tip = content.tooltip != null ? content.tooltip : "";
                if (entry != null && !(entry.BoxedValue is bool)) {
                    string dft = FormatDefaultValue(entry.DefaultValue);
                    if (!string.IsNullOrEmpty(dft) && tip.IndexOf("默认", StringComparison.Ordinal) < 0) {
                        tip += (tip.Length > 0 ? "\n" : "") + DefaultSuffix(entry);
                    }
                }
                if (hk != null) {
                    //绑定的键（录制中为 [...]）直接跟在标签后；标签宽度按需放宽，避免固定宽度下被挤到下一行
                    string txt = content.text + KeySuffix(hk);
                    tip += (tip.Length > 0 ? "\n" : "") + T("右键：绑定/删除快捷键", "Right-click: bind/remove hotkey");
                    c2 = new GUIContent(txt, tip);
                } else if (tip != content.tooltip) {
                    c2 = new GUIContent(content.text, tip);
                }
                //宽度：只按文字实际宽度占位（+内边距）。以前这里固定预留一大段宽度给"复选框标签上的快捷键"，
                //快捷键已改到右侧同伴控件/独立键位框，标签再留着那段空白就只是把复选框推远 -> 现在收缩掉。
                //只收缩、不放大（除非本行带键位后缀，后缀可能比预留宽度还长，那时按需放宽到窗口可用宽度）。
                // R385 说明：这里的 w 是**上限**，实际宽度会被下面的 need 收缩到"刚好装下文字"。
                //  所以自绘页传固定宽度（Sc(140)/Sc(200)）也不会让控件离标签太远,
                //  真正决定"控件贴不贴标签"的是绘制宽度 w，而它已经被收缩成文字宽度了。
                float need = _label.CalcSize(c2).x + Sc(8);
                if (need < w) w = Mathf.Max(Sc(24), need);
                else if (hk != null) w = Mathf.Min(need, Mathf.Max(w, _winWidth - SidebarWidth() - Sc(24)));
            } catch (Exception __ex) { Guard.Log("条目默认值处理", __ex); }
            if (GUILayout.Button(c2, _label, GUILayout.Width(w), GUILayout.Height(h))) {
                //只认左键：右键是"绑定快捷键"的手势，不能顺手把值重置为默认（会造成数值/勾选闪变）
                Event evLbl = Event.current;
                if (evLbl == null || evLbl.button == 0) {
                    if (entry != null) { try { entry.BoxedValue = entry.DefaultValue; } catch (Exception __ex) { Guard.Log("恢复条目默认值", __ex); } }
                    MarkKeyChanged();   //重置默认值可能改了键位：让序列监听的 watch 列表失效
                    _editText.Remove(entry);
                    _editOpen.Remove(entry);
                    if (_capturing == entry) _capturing = null;
                }
            }
            //右键命中区：标签 + 右侧 44px（覆盖紧邻的复选框），整行右键都能绑
            if (hk != null) RegisterRowHotkey(hk);
        }

        private static string FormatDefaultValue(object v) {
            try {
                if (v == null) return "";
                if (v is bool) return "";
                if (v is KeyCode) return KeyDisplayName((KeyCode)v);
                if (v is Enum) return EnumDisplayName(v.ToString());
                if (v is float f) return f.ToString("0.##", CultureInfo.InvariantCulture);
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            } catch { return ""; }
        }

        private static ConfigFile _visCfg;
        private static int _visVer = -1;
        private static List<ConfigEntryBase> _visCache;
        private static List<ConfigEntryBase> VisibleEntries(ConfigFile config) {
            if (config == null) return new List<ConfigEntryBase>();
            //按条目版本缓存：外部插件页每个 OnGUI 事件都会调它，原来每次新建列表 + 全表扫描
            if (_visCache != null && _visCfg == config && _visVer == _entryVersion) return _visCache;
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            foreach (ConfigEntryBase e in AllEntries(config)) {
                result.Add(e);
            }
            _visCfg = config; _visVer = _entryVersion; _visCache = result;
            return result;
        }

        //配置条目表。BepInEx 5.4.23 起 GetConfigEntries() 被标为过时（提示 "Use Values instead"）：
        //ConfigFile 显式实现了 IDictionary<ConfigDefinition, ConfigEntryBase>，过时提示里说的 Values 就是它
        //（这个版本**没有**公开的 Values 属性，只有这个显式接口成员）。取 Values 还省掉了原来 ToArray 的复制。
        //老版本 BepInEx 若没有实现该接口，仍回退到过时方法（用 pragma 压掉警告，保证跨版本都能读到条目）。
        private static readonly ConfigEntryBase[] NoEntries = new ConfigEntryBase[0];
        private static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) {
            if (config == null) return NoEntries;
            IDictionary<ConfigDefinition, ConfigEntryBase> dict = config as IDictionary<ConfigDefinition, ConfigEntryBase>;
            if (dict != null) return dict.Values;
#pragma warning disable CS0618 //过时兜底：仅当该 BepInEx 版本没实现 IDictionary 时才走到
            return config.GetConfigEntries();
#pragma warning restore CS0618
        }

        //侧栏宽度：0 = 自动，>0 = 用户拖出的宽度
        private static float _sidebarW = 0f;
        private static bool _sidebarResizing;
        private static ConfigEntry<int> _sidebarWEntry; //设置页栏目宽度滑块（0=自动
        private static ConfigEntry<string> _sidebarOrderEntry; //侧栏栏目自定义顺序（逗号分隔）, 拖动栏目自动写回
        //侧栏栏目拖拽排序：**不做坐标命中判定**。
        //  条目画在 BeginArea -> ScrollView 之内，而鼠标事件是屏幕坐标；中间的标题栏占位、
        //  水平布局缩进、滚动偏移都没有稳定的公开换算, 这正是"长按拖不动 / 无法自定义排序"的根因。
        //  改用两个与坐标空间无关的可靠信号：
        //   1) 按下的是哪一项：画完每个按钮后读 GUIUtility.hotControl（MouseDown 当帧由被按下的按钮取得）；
        //   2) 拖到了第几行：鼠标纵向位移 ÷ 行高（位移在任何坐标空间里都相等）。

        //按自定义顺序重排侧栏栏目（未记录的新栏目保留相对位置、排到末尾）
        //按自定义顺序重排侧栏栏目（未记录的新栏目排到末尾）。
        // #2 这里是**唯一**产出三份列表的地方：
        //   _allSections = 全量（含隐藏），给自定义栏目的表头当列；
        //   _userHiddenSections = 名字带 "!" 前缀的那些；
        //   _internalSections = 全量减隐藏 -> 侧栏真正显示的。
        private static void ApplyCustomSidebarOrder() {
            try {
                string ord = _sidebarOrderEntry != null ? _sidebarOrderEntry.Value : null;
                List<string> all = new List<string>();
                HashSet<string> hid = new HashSet<string>();
                // #3 先把整串里的段名按"改名映射"改写一遍，再做死条目判定。
                //  为什么必须改写而不是直接删：栏目**改名**（如 Builder Enhancements -> build）时，
                //  老的 Sidebar Order 里留的是旧段名。它匹配不到任何真实栏目 -> 会变成占位的死条目，
                //  而改名后的新栏目又不在串里 -> 被追加到末尾，于是"建造"跑到了错误位置。
                //  改名 = 同一栏目的延续，用户的排序意图应当保留，所以映射过去而不是丢弃。
                if (!string.IsNullOrEmpty(ord)) ord = RemapSidebarOrderNames(ord);
                // **#3 死条目剔除**：串里匹配不到任何真实栏目的名字（既不是注册项，也不是可见 cfg 段）
                //  一律丢掉。留着只会白占一个位置，还会让新栏目被挤到末尾。
                //  注意必须在字母序判定**之前**做：字母序判定要求串里全是真栏目，
                //  一个死条目就能让它误判成"非字母序的自定义顺序"，从而继续压住默认 rank 顺序。
                if (!string.IsNullOrEmpty(ord)) ord = DropDeadSidebarOrderNames(ord);
                // **#2 修复历史污染**：早期版本的"字母序兜底"曾被核心的 applyOrder 落盘，
                //  cfg 里因此留下一份**纯字母序**的 Sidebar Order。它不是用户自定义
                //  （没人会把栏目排成字母序），却会永久覆盖默认 rank 顺序 -> 这里识别并忽略它。
                if (!string.IsNullOrEmpty(ord)) {
                    List<string> probe = new List<string>();
                    foreach (string raw0 in ord.Split(',')) {
                        string n0 = raw0.Trim();
                        if (n0.Length == 0) continue;
                        if (n0[0] == '!') n0 = n0.Substring(1).Trim();
                        if (n0.Length > 0 && !probe.Contains(n0)) probe.Add(n0);
                    }
                    List<string> sorted = new List<string>(probe);
                    // #4 必须用 OrdinalIgnoreCase 而不是 Ordinal：栏目改名后新段名大小写可能变了
                    //  （Builder Enhancements -> build）。Ordinal 下小写 'b'(0x62) 排在所有大写之后，
                    //  于是一份真正的字母序污染串在改名后**不再**被识别成字母序 -> 判定失效，
                    //  那份非用户自定义的顺序又会永久压住默认 rank 顺序（正是本次要修的症状）。
                    //  段名只由 ASCII 字母/空格组成，忽略大小写比较不会引入歧义。
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
                // #1 关键修复：这里**不能**再按键名字母排序！
                //  RebuildSections 已按各功能注册的 SR.Nav(键, rank) 排好默认顺序；
                //  原来"未记录的栏目按字母排序补到末尾"会把这份 rank 顺序整个覆盖掉
                //  -> 表现就是"默认排序永远不生效"（因为 cfg 里的 Sidebar Order 一开始是空的）。
                //  现在：空 -> 保持 rank 顺序；非空 -> 追加新增栏目时同样保持其相对位置（排到末尾）。
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
                // #3 清洗后与干净默认值一致 -> 直接落盘把死条目清掉，别让它每帧都重演。
                //  只在"确实变干净了"时写：正常的用户自定义顺序不会被抹掉。
                string cleaned = string.Join(",", _internalSections.ToArray());
                if (_sidebarOrderEntry != null && !string.IsNullOrEmpty(ord)
                    && _sidebarOrderEntry.Value != null && _sidebarOrderEntry.Value != cleaned
                    && DropDeadSidebarOrderNames(_sidebarOrderEntry.Value) == cleaned)
                    _sidebarOrderEntry.Value = cleaned;
            } catch (Exception __ex) { Guard.Log("应用侧栏自定义顺序", __ex); }
        }

        // **#3 侧栏顺序串的段名改名映射**：老段名 -> 新段名。
        //  与 ConfigMigration.SectionMap 是两件事：那边迁的是 cfg 的**段**（[Section]），
        //  这边迁的是 Settings / Sidebar Order 这个**逗号分隔的值**里的名字,
        //  后者不在 SectionMap 的管辖范围内，只能单独处理，否则改名后栏目排序会丢。
        private static readonly Dictionary<string, string> SidebarOrderRenames = new Dictionary<string, string> {
            { "建造增强", "build" },
            { "Builder Enhancements", "build" },
        };

        //把 Sidebar Order 值里的旧段名按映射改写成新段名（保留 ! 前缀与原顺序）。
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

        //剔除 Sidebar Order 里匹配不到任何真实栏目的死条目，返回干净后的串。
        //判定依据 = RebuildSections 产出的 _internalSections（注册项 + 兜底 cfg 段，已排除隐藏项）。
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

        // #2 设置页的栏目顺序块（在外部插件分区之上）：**按住行拖动**即可调整顺序。
        //与侧栏那套的关键区别：这里用**左键**。IMGUI 的 MouseDrag/MouseUp 对左键是可靠的，
        //当初"右键长按拖不动"的根因正是右键拿不到拖动/松开事件, 换成左键就不存在这个问题。
        //（ G9：原 _ordFrom/_ordTo/_ordDrag/_ordPrev/_ordCur 五个字段已随 HandleSidebarOrderDrag 一并删除。
        //  它们只在那个无调用点的方法里被用到，其中 _ordPrev/_ordCur 更是"只有读、没有写"。
        //  现在的栏目排序状态由 SR.TableArgs 持有, 见下方 EnsureOrdArgs/RenderSidebarOrder。）

        // #3 自定义栏目= **一个没有数据行的 SR 列表**（只用它的表头）：
        //  · 表头列 = 侧栏栏目名；拖动列头 = 改顺序；右键列头 = 隐藏该栏目；Shift+右键 = 还原全部。
        //  三种行为全部由核心表格提供，不再自己实现一套拖拽。
        private static readonly List<int> _ordVis = new List<int>();
        private static readonly HashSet<int> _ordHidden = new HashSet<int>();
        // #1 真因：拖拽/列宽状态（dragCol/dragX0/moveCol/moveDx...）都是 TableArgs 的**实例字段**，
        //  每帧 new 一个 -> 跨帧状态永远丢 -> 表现就是"不能拖、不能改列宽、啥都不能干"。
        //  核心化没问题：它本来就把状态交给调用方持有（联机页/EX 都是 static 字段），是我这里漏了。
        private static SR.TableArgs _ordArgs;
        private static int _ordN = -1;
        private static float[] _ordColW;
        private static float[] _ordDefaultColW() { int n = Mathf.Max(1, _ordN); float[] d = new float[n]; for (int i = 0; i < n; i++) d[i] = 64f; return d; }
        private static int[] _ordOrder;   // **换列必需**：显示顺序（数据列下标）
        private static Vector2 _ordScroll;   // **#3 列表内部的滚动位置**
        private static void EnsureOrdArgs(int n) {
            if (_ordArgs != null && _ordN == n) return;
            _ordN = n;
            _ordArgs = new SR.TableArgs();
            _ordColW = new float[n];
            for (int i = 0; i < n; i++) _ordColW[i] = 64f;   // 每个表头宽度默认 64px（设计像素；可拖分隔线拉宽）
            _ordArgs.colW = _ordColW;
            _ordArgs.defaultColW = _ordDefaultColW();   // **#1 Shift+右键还原列宽用**
            _ordArgs.rowCount = 0;        // **没有数据行**：只用表头
            _ordArgs.emptyHint = null;
            _ordArgs.headerH = Sc(22); _ordArgs.rowH = Sc(22); _ordArgs.rowsH = 0f;
            // #5 竖向列表已按需求撤掉，回到**横向列头**（每个表头 40px；可拖分隔线拉宽）
            _ordArgs.vertical = false;
            _ordArgs.hideable = true; _ordArgs.reorderable = true; _ordArgs.resizable = true;
            _ordArgs.vis = _ordVis; _ordArgs.hidden = _ordHidden;
            // **换列必需**：把当前显示顺序作为 order（原来漏了这一项 -> 核心换列算法拿不到 order）
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
                    // #2 核心会在重建后调用 applyOrder 做"归一化"；那种调用传回来的就是当前顺序，
                    //  落盘只会把"当前显示顺序"固化进配置（历史上就是这么把字母序写进去的）-> 相同则跳过。
                    bool same = _ordOrder != null && _ordOrder.Length == o.Length;
                    for (int i = 0; same && i < o.Length; i++) if (_ordOrder[i] != o[i]) same = false;
                    if (same) return;
                    string[] names = new string[_ordN];
                    for (int i = 0; i < _ordN; i++) names[i] = SR._allSections[o[i]];
                    SR._sidebarOrderEntry.Value = string.Join(",", names);
                    // #1 "时好时坏"的真因：o 是**列下标的一个置换**，而紧接着 RebuildSections 会把
                    //  _allSections 重排成新序, 此时"显示第 i 列"已经对应 _allSections[i]，
                    //  再把置换 o 留在 _ordOrder 里，下一帧核心就拿着**过期的映射**去算 from/to
                    //  -> 有时能移到想要的位置，有时移错/不动。正确做法：重建后 order 归位为恒等序。
                    _ordOrder = null;   //置空 -> EnsureOrdArgs/下一次绘制时重建为恒等序
                    // 写完顺序必须**立刻重建侧栏**：否则 _internalSections 还是旧序（表现为"拖了没反应"）
                    SR.RebuildSectionsPublic(false);
                } catch (Exception __ex) { Guard.Log("保存栏目顺序", __ex); }
                };
            _ordArgs.resetAll = delegate { SR.ClearHiddenSections(); };   //Shift+右键 = 还原全部
            _ordArgs.saveWidths = delegate { };   //列宽改动由静态 _ordColW 保留（跨帧不丢）
        }

        private static void RenderSidebarOrder() {
            try {
                if (SR._allSections.Count <= 1) return;
                GUILayout.Label(T("- 自定义栏目 -", "- Custom sections -"), _secHeader);
                SR.WrapLabel(T("拖动列头调整侧栏顺序；右键列头隐藏栏目；Shift+右键还原。",
                                  "Drag a header to reorder; right-click a header to hide a section; Shift+right-click to restore."));
                int n = SR._allSections.Count;
                EnsureOrdArgs(n);
                // #6 高度**贴合内容**：原来的固定 Sc(96) 是"竖向列表"时代留下的（那时列头是竖排的一长条，
                //  靠固定高度把它们关在框里）。改成横向列头后，列头只有 Sc(24) 高 -> 96 里剩下的 ~70px
                //  全是空白，就是"自定义栏目底下空一大块"的来源。横向溢出仍由 BeginScrollView 兜住。
                GUILayout.BeginVertical(GUILayout.Height(Sc(30)));
                SR.TableArgs a = _ordArgs;
                _ordHidden.Clear(); _ordVis.Clear();
                for (int i = 0; i < n; i++) {
                    if (SR._userHiddenSections.Contains(SR._allSections[i])) _ordHidden.Add(i); else _ordVis.Add(i);
                }
                //sum = 可见列头的**总宽**（列头是横向排的，不是高度）,  传给 GetRect 当盒宽；
                //溢出时由列表内部的横向滚动兜住（不再靠"固定高度"）。
                float sum = 0f; for (int i = 0; i < _ordVis.Count; i++) sum += SR.Ctl.Sc(_ordColW[_ordVis[i]]);
                if (sum < Sc(60)) sum = Sc(60);
                GUILayout.Space(Sc(2));
                //列表内部滚动：列头横向排不下时在这里滚（滚动条样式传 none = 不显示条，滚轮仍可用）
                _ordScroll = GUILayout.BeginScrollView(_ordScroll, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none);   // **#4 列表内滚动条也隐藏**
                a.box = GUILayoutUtility.GetRect(sum, Sc(24));   // 横向列头：高度固定 24px -> **不占用页面的纵向空间**
                a.stripW = sum;
                if (_ordOrder == null) { _ordOrder = new int[n]; for (int i = 0; i < n; i++) _ordOrder[i] = i; a.order = _ordOrder; }   // **#1 恒等序兜底**
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
        // 栏目顺序的行拖拽：左键按下 -> 拖动高亮目标行 -> 松手落位。
        // 按键状态取自 IO 层（g.IO.MouseClicked[0]），而不是 IMGUI 事件流 —— 后者会被 Button、ScrollView 用 Use()
        // 吃掉，时序不可靠。这是 ImGui 的正典做法（ButtonBehavior, imgui_widgets.cpp:552）。
        // **G9**：原来的 HandleSidebarOrderDrag() 已删 —— 它全仓无调用点，且 _ordPrev/_ordCur 这两个行矩形缓冲
        // 从来没被写过，所以这段拖拽从一开始就没生效过。真正的排序入口是设置页自定义栏目（TableDraw + 拖列头）。

        // 侧栏栏目长按右键拖动排序
        // 真因（反复失败的原因）：GUILayout.Button 会把 MouseDown/MouseUp 事件 Use() 掉,
        //  Unity 的按钮对**任意**鼠标键按下都取 hotControl 并 Use()，Use() 之后事件类型变成 Used。
        //  所以"在按钮之后判定鼠标事件"永远读不到按下/松开（e.type 已不是 MouseDown/MouseUp）。
        //（注：侧栏右键拖拽已整体移除，排序改在设置页栏目顺序里用左键拖拽完成）
        //  因此正确做法是：判定放在按钮之前 + 矩形用双缓冲（Prev 供判定，Cur 本帧收集）。

        private static float SidebarWidth() {
            //两个来源都要生效：拖动中用即时值 _sidebarW，否则读栏目宽度配置项（只读其一都会卡住）
            float w = 0f;
            if (_sidebarResizing) w = _sidebarW;
            if (w <= 0f && _sidebarWEntry != null) w = _sidebarWEntry.Value;
            if (w <= 0f) w = _sidebarW;
            // **先判断再算**：CalcAutoSidebarWidth 要遍历所有栏目名/外部插件名逐个 CalcSize，
            //而 SidebarWidth 在布局里每帧被调用十几次, 用户拖过宽度（w>0）时 auto 的结果根本用不到，
            //原来却每次都白算一遍。
            if (w > 0f) return Mathf.Clamp(w, 100f, 320f);
            return CalcAutoSidebarWidth();
        }

        private static int _autoSbKey;   // **R411**：从拼接长字符串改为哈希折叠
        private static float _autoSbW = -1f;
        private static float CalcAutoSidebarWidth() {
            //按集合/语言/缩放缓存：CalcSize 逐个栏目实测较贵，而自动宽度是默认路径且每帧被调用十几次
            // **R411**：缓存键从"拼接全部栏目名/插件名的长字符串"改成哈希折叠。
            //  结果虽然有缓存，但**键本身**原来每帧都要重建（SidebarWidth 在布局里每帧被调十几次，
            //  默认自动宽度下每次都遍历全部栏目名做字符串 +=）。string.GetHashCode 有缓存 -> 折叠零分配。
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

        // **R392**：当前 RenderControl 的调用来源（是否内部条目），供滑块路径判断
        private static bool _curIsInternal = true;
        // **R393**：同行附加控件的本帧已画过记录（条目 + 帧号），防重复渲染
        private static ConfigEntryBase _followDoneEntry;
        private static int _followDoneFrame = -1;

        private static float EntryNameWidth() {
            //缓存：模式/分区/语言/缩放/外部插件不变时列宽不变，避免每帧 CalcSize
            //语言用 LangVer（版本号）而不是 _langEn：外部语言包下 _langEn 恒为 false，
            //从中文切到语言包时 _langEn 没变、但 ZhKey 的译文长度变了 -> 不加版本号列宽不会重算，
            //长译文会被按旧语言算出来的列宽截断。
            // **必须带 _entryVersion**：条目表变了（惰性 Bind 出新条目）列宽才重算。
            //少了它：新条目的名字更长却仍按旧列宽渲染 -> 长名字被截断（表现为"名字显示一半"）。
            // **R411**：缓存键从"拼接长字符串"改成哈希折叠（本方法每帧至少两趟各一次，拼串是白分配）。
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

        // **R411**：本行名称的实测宽度按条目缓存（_nameLabel 是 wordWrap=true 的样式，测量较贵）。
        //  IMGUI 一帧至少 Layout+Repaint 两趟，每行每趟都测一次；语言/缩放/条目不变时结果不变。
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

        //某 section 的可见条目（功能自绘页用它拼自己的页面；自动应用功能注册的行可见性）
        //性能：原来每次调用都 new List + 全表遍历；调用方（BuildingTools / Chat / ExModule 地图网格）
        //每帧各一次、IMGUI 一帧还有 Layout+Repaint 两趟 -> 按 section 缓存，条目表版本变化时整体失效。
        private static readonly Dictionary<string, List<ConfigEntryBase>> _secCache = new Dictionary<string, List<ConfigEntryBase>>();
        private static readonly Dictionary<string, int> _secCacheVer = new Dictionary<string, int>();
        internal static List<ConfigEntryBase> SectionEntries(string section) {
            int ver = _entryVersion;
            List<ConfigEntryBase> cached;
            int oldVer;
            // **显式两步，不依赖求值顺序**：原来写成
            //    _secCache.TryGetValue(sec, out cached) && _secCacheVer.TryGetValue(sec, out oldVer) && oldVer == ver
            //  它**当前是对的**（C# 短路 + out 参数"最后赋值胜出"），但正确性完全押在求值顺序语义上,
            //  任何人做一次看似无害的重构（拆行、调换顺序）就会漏出**陈旧列表**，
            //  表现为"配置项改了但界面不更新"，且极难复现。拆开写后命中条件只剩"版本一致且非空"。
            //（顺带加 cached != null：TryGetValue 返回 true 而值为 null 在字典里是合法的。）
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

        //条目表版本 + 查找缓存：FindInternalEntry 原来是"每次线性全表扫描"（AllEntries 每调用一次都新建枚举器）。
        //每帧调用点很多（QuickAdjust 9 处、Level 3、Experiments 3、ExModule 2、联机 3、通用条目行 2），
        //IMGUI 一帧至少 Layout + Repaint 两趟 -> 这里按 section+key 做字典缓存。
        //注意：组合键 / [Reflection] / 自动快捷键等条目是**惰性 Bind** 的，所以：
        //  · 未命中必须回退扫描；
        //  · **只把命中结果写进缓存**（查不到就不记，避免把"还没绑定"永久记成 null）；
        //  · SR 自己新增绑定处调 NoteEntryBound() 让 SectionEntries 的缓存失效。
        private static readonly Dictionary<string, ConfigEntryBase> _entryCache = new Dictionary<string, ConfigEntryBase>();
        private static int _entryVersion;

        internal static void NoteEntryBound() { _entryVersion++; }

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
                    _entryCache[k] = e;   //只缓存命中（未命中不记，惰性绑定的条目下次还能查到）
                    return e;
                }
            }
            return null;
        }

        private static string FormatDefaultValue(ConfigEntryBase entry) {
            try {
                if (entry == null || entry.DefaultValue == null) return "";
                object dv = entry.DefaultValue;
                //开/关 也要走 UseEn（不能写 _langEn）：外部语言包 FallbackId="English" 时 _langEn=false，
                //写死 _langEn 会显示开/关，却配上 T() 给出的 "Default: " 英文前缀，中英混排。
                if (dv is bool) return ((bool)dv) ? (UseEn ? "ON" : "开") : (UseEn ? "OFF" : "关");
                if (dv is float) return ((float)dv).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                if (dv is int) return ((int)dv).ToString();
                //KeyCode 也是 Enum：必须先判 KeyCode，否则这一支是死代码、默认值丢失中文键名
                if (dv is KeyCode) return KeyDisplayName((KeyCode)dv);
                if (dv is System.Enum) return EnumDisplayName(dv.ToString());
                return Convert.ToString(dv, System.Globalization.CultureInfo.InvariantCulture);
            } catch { return ""; }
        }

        //把某栏目的通用设置条目整段渲染出来：供"注册了自绘页、但仍要保留通用设置列表"的栏目用
        //（方块破坏的普通页就是原通用列表，高级页才是候选表）。
        //按段/键渲染单个通用条目行：供自绘页把某个配置项以"通用条目"样式嵌进去（如高级页的自动刷新）
        //自绘页用：内联布尔开关（与设置页同款✓方框），标签由调用方给（便于改名/并排摆放）
        public static void RenderSectionEntries(string section, System.Func<ConfigEntryBase, bool> filter = null) {
            try {
                float colWidth = EntryNameWidth();
                List<ConfigEntryBase> list = SectionEntries(section);
                for (int i = 0; i < list.Count; i++) {
                    if (filter != null && !filter(list[i])) continue;   //页面可排除属于别的子页的条目
                    RenderEntryRow(list[i], true, colWidth);
                }
            } catch (Exception __ex) { Guard.Log("渲染栏目设置(" + section + ")", __ex); }
        }

        private static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) {
            //null 条目：调用方（功能自绘页/EX）常用 FindEntry 的结果直接传进来，
            //而条目可能因为功能未初始化/键改名而取不到 -> 直接返回，否则下面 entry.Definition 会 NRE。
            if (entry == null) return;
            string name = isInternal ? ZhKey(entry) : entry.Definition.Key;
            //描述：中文模式用 ZhDesc/配置描述；英文模式只用 ZhDesc 的英文表（查不到留空）
            string desc;
            if (UseEn) {
                desc = isInternal ? ZhDesc(entry) : null;
                //外部插件的说明来自它自己的 ConfigDescription（本身是英文），不能因为"界面是英文"就丢掉 -> 否则 tooltip 恒空
                //只有外部插件条目才回退它自己的 ConfigDescription（内部条目的说明应是英文表/空，
                //不能回退 Bind 时传入的中文，否则英文界面出现中文 tooltip）
                if (desc == null && !isInternal && entry.Description != null) desc = entry.Description.Description;
                if (desc == null) desc = "";
            } else {
                string zhDesc = isInternal ? ZhDesc(entry) : null;
                desc = zhDesc != null ? zhDesc
                    : (entry.Description != null && entry.Description.Description != null ? entry.Description.Description : "");
            }
            //名称列宽自适应：
            //  · 先按名称 + [键]的实际文字宽度给足（不换行）；
            //  · 该行控件实际占多宽就从可用空间里扣多少（复选框只有 26，键位/下拉约 170），
            //    这样长名字（如移动轨迹总开关 [1]）不会因为固定预留 185 而被挤到第二行。
            float reserve = Sc(180);
            object cval = entry.BoxedValue;
            if (cval is bool) reserve = Sc(34);
            else {
                float hookW = SR.RowComboWidth(entry);
                if (hookW > 0f) reserve = Sc(hookW + 8);
            }
            // R385：标签宽度 = **本行文字实测宽度**（不是整栏统一宽）。
            //   用户要求控件紧贴自己的标签-> 标签画多宽，控件就从标签右边缘紧接着开始。
            //   若用整栏统一宽（EntryNameWidth），短名行会在标签右侧留一段空白，
            //   控件被推离标签, 正好是用户不要的效果。
            //   nameW 只是**上限**（防止超长名字把控件挤出可视区），超了才截断/换行。
            float avail = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(24));
            float textW = NameTextWidth(entry, name);   // **R411**：按条目缓存（见 NameTextWidth）
            float cap = Mathf.Max(Sc(50), avail - reserve);
            float nameW = Mathf.Min(Mathf.Max(colWidth, Sc(50)), cap);
            float nameTw = Mathf.Min(textW, nameW);   //绘制宽度 = 文字宽（夹在上限内）
            //名称列包在垂直组里，标签才能拿到完整高度：直接放在 BeginHorizontal 只给单行高，
            //CJK 字形下沉/换行会被裁剪。
            // **R411**：Begin/End 成对兜底（R410-E1）。中间任何一步抛异常若不闭合分组，
            //  Unity 会从这一帧起持续报 "Getting control N's position in a group with only M controls"，
            //  后续控件全落错分组。RenderEntryRow 是所有条目行的必经之路，必须兜住。
            GUILayout.BeginHorizontal();
            try {
                GUILayout.BeginVertical(GUILayout.Width(nameTw));
                try {
                    //整串走 RowTip（按 entry 缓存；语言/语言包一变由 NameCacheSync 清空）：
                    //原来这里每帧每条目手工拼三段（说明 + 默认值 + 恢复提示），而 RowTip 就是为它写的缓存版，
                    //写好了却一直没被调用（等于死代码），拼串开销白付。
                    string tip = RowTip(entry, desc);
                    if (GUILayout.Button(new GUIContent(name, tip),
                        _nameLabel, GUILayout.Width(nameTw), GUILayout.Height(TextHeight(name, nameTw)))) {
                        //只认左键：右键是"绑定快捷键"的手势，绝不能顺手把值重置为默认（会造成数值/勾选闪变）
                        Event evName = Event.current;
                        if (evName == null || evName.button == 0) {
                            try { entry.BoxedValue = entry.DefaultValue; } catch (Exception __ex) { Guard.Log("恢复条目默认值(点击名称)", __ex); }
                            // **G8 修复**：与 RestoreLabel 的同类分支保持对称。复位可能改了键位，而序列型快捷键的
                            //  匹配依赖 _watchKeys（由 MarkKeyChanged 置脏后重建）-> 漏了这句时，带"序列前键"的
                            //  条目在点名称恢复默认后，该序列快捷键可能一直触发不了（要等下一次别的注册置脏）。
                            MarkKeyChanged();   //重置默认值可能改了键位：让序列监听的 watch 列表失效
                            _editText.Remove(entry);
                            _editOpen.Remove(entry);
                        }
                    }
                } finally { GUILayout.EndVertical(); }
                // R385 排版铁律（用户明确要求，全项目统一）：**每个控件都紧贴它自己的标签右侧**。
                //   · 名称标签的绘制宽度就是 nameTw（按该行文字实测），控件紧接在它后面 -> 完全相邻；
                //   · 右侧留白由行尾自然形成，不插 FlexibleSpace（那会把控件推到行最右）；
                //   · 同理**不做主控件列对齐补白**, 那会在控件与标签之间塞一段空白，反而变成"不挨着"。
                //     （R384 曾加过 ControlColumnWidth 补白，本轮按用户要求撤销。）
                GUILayout.Space(Sc(6));
                RenderControl(entry);
                RenderFollowUp(entry, isInternal);
            } finally { GUILayout.EndHorizontal(); }
            GUILayout.Space(Sc(2));
        }

        // **R392**：同一行的同伴键位与附加控件紧跟主控件绘制（不插多余间隔）。
        //  滑块路径会**先 EndHorizontal 再 return**，所以它必须在自己的水平组内调这个方法，
        //  否则附加控件会被拆到别的行去（用户反馈"快捷键框不挨着滑块条"）。
        private static void RenderFollowUp(ConfigEntryBase entry, bool isInternal) {
            try {
                // **R393 去重**：滑块路径（RenderControl 内）与外层 RenderEntryRow 都会调本方法，
                //   同一个条目在同一帧里会被渲染**两次** -> 聊天框的快捷键框出现两个（用户反馈）。
                //   IMGUI 一帧可能多次进入本页，所以用条目 + 帧号判重而不是布尔标记。
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

        //文本高度 + CJK 字形下沉余量，避免标签裁剪（按 文本+宽度+缩放 缓存）
        // **R411**：缓存键从"拼字符串"改成两级字典（外层文本、内层 [宽,缩放,高] 数组）。
        //  原来命中缓存也要先 text + "|" + width + "|" + _scaled 拼一个串，每行每帧都分配。
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

        //Dear ImGui 风格水平滑块：细轨道 + 填充段 + 圆形把手（三态反馈）；
        //左键拖拽或点击轨道跳转，悬停/拖拽滚轮微调。
        //数值在松开左键时才提交，拖动过程只预览不写配置。
        //**R415**：原来这里只有一个跨帧布尔 `_sliderCommitted`，页面上有多条滑块时会互相误读
        //（谁先读到算谁的，值可能写到**别的条目**上；而侧栏宽度那条路径干脆既不读也不清）。
        //现在改成「帧号 + 几何 + 值」：MouseUp 那一趟记下是谁提交了、提交到哪条滑块、提交了什么值，
        // ================================================================
        // **R416-1 滑块重做：拖动输入交给 Unity 原生 GUI.HorizontalSlider**
        //
        // 为什么推倒重写（R413->R415 三轮都失败的根本原因）：
        // 之前这套是自己手写拖动状态机 —— 全局 `_sliderDragActive` + `_sliderDragRect`
        // 记录"哪条滑块在拖"，再在 MouseUp 那一趟用"帧号 + 几何"把提交信号传给调用方。
        // 这个设计有三个**结构性**缺陷（不是补一两个判断能解决的）：
        //   1. IMGUI 一帧有多趟（Layout -> MouseDown -> MouseDrag -> MouseUp -> Repaint），
        //      同一帧内"提交信号"的读写时序对IMGUI 的事件顺序极度敏感；
        //   2. 全局单例状态意味着**页面上第二条滑块会抢走第一条的提交信号**（几何相等时尤其）；
        //   3. 提交发生在 MouseUp 那一趟，而调用方的 `SliderCommitFor` 在同一趟的**后面**才跑 ——
        //      中间只要有任何别的控件做了 `ev.Use()` 或抛异常，信号就丢了，表现就是"拖完弹回"。
        //
        // 现在：输入完全交给 `GUI.HorizontalSlider`。它由 Unity 自己管 `GUIUtility.hotControl`，
        // 跨趟稳定、鼠标移出也能继续拖、值随拖动实时变化 —— 这些都是引擎保证的。
        // 我们只做两件事：
        //   · 自绘外观（轨道 / 填充 / 把手）叠在下面，保持原来的样子；
        //   · 用"返回值与传入值不同"直接提交 —— 这就是 ImGui 的做法（`ImGui::SliderFloat`
        //     内部也是 `value = DrawSlider(...)` + `if (value != v_last) value_changed = true`）。
        // 提交判断变成"这一趟算出来的值变了就写"，没有任何跨帧状态可被冲掉。
        // ================================================================

        //原生 slider 用的两个样式（只要它的输入/热区，不要它的外观 —— 外观我们自己画）
        private static GUIStyle _sliderNative;
        private static GUIStyle _sliderNativeThumb;
        //上次提交给配置的值（用来抑制"没动却每帧写盘"）
        private static readonly System.Collections.Generic.Dictionary<ConfigEntryBase, float> _sliderLastWritten =
            new System.Collections.Generic.Dictionary<ConfigEntryBase, float>();

        // P1 帧末清理：切场景/复位时清掉"展开当帧不画"的记录。
        // **R416**：滑块不再需要帧末清理 —— 拖动状态由 Unity 原生 hotControl 自己管，
        // 提交是"值变了就写"，没有跨帧状态可清。
        internal static void SliderFrameEndCleanup() {
            _comboSkipFrame.Clear();   // **R414**：换场景/复位时清掉"展开当帧不画"的记录
        }

        private static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false, float step = 0f) {
            float trackH = Sc(4), handleD = Sc(16);
            float trackW = Mathf.Max(1f, rect.width - handleD);

            // ------------------------------------------------------------------
            // 输入层：原生 HorizontalSlider。传一个**看不见**的样式（无 background），
            // 这样它只吃事件与热区、不画任何东西，外观完全交给我们自绘。
            // 它自己管 hotControl，所以"拖动中鼠标移出滑块"也能跟随，跨趟稳定。
            // ------------------------------------------------------------------
            if (_sliderNative == null || _sliderNativeThumb == null) {
                _sliderNative = new GUIStyle();
                _sliderNativeThumb = new GUIStyle();
                _sliderNative.normal.background = null;
                _sliderNativeThumb.normal.background = null;
            }
            float raw = GUI.HorizontalSlider(rect, value, min, max, _sliderNative, _sliderNativeThumb);

            // **R414**：int 条目原来的 Mathf.Round 在大跨度下等于"拖不动"
            // （半径 10 万~100 万、轨道约 150px，每像素差 6700，round 之后看起来完全不变）。
            // 按调用方给的 step 量化；step<=0 才退回整数取整。
            float nv = raw;
            if (step > 0f) nv = Mathf.Round(nv / step) * step;
            else if (intStep) nv = Mathf.Round(nv);
            nv = Mathf.Clamp(nv, min, max);

            // 几何按最终值算：轨道两端各留半个把手，使最小/最大时把手完整落在 rect 内
            float t = Mathf.Clamp01(Mathf.InverseLerp(min, max, nv));
            Rect trackRect = new Rect(rect.x + handleD / 2f, rect.y + (rect.height - trackH) / 2f, trackW, trackH);
            Rect fillRect = new Rect(trackRect.x, trackRect.y, trackW * t, trackH);
            Rect handleRect = new Rect(rect.x + trackW * t, rect.y + (rect.height - handleD) / 2f, handleD, handleD);
            GUI.Box(trackRect, GUIContent.none, _sliderTrack);
            if (fillRect.width > 0.5f) GUI.Box(fillRect, GUIContent.none, _sliderFill);
            //把手三态：拖动中（原生 slider 拿到hotControl）/ 悬停 / 常态
            bool dragging = GUIUtility.hotControl != 0 && rect.Contains(Event.current.mousePosition);
            bool hover = rect.Contains(Event.current.mousePosition);
            GUIStyle hs = dragging ? _sliderHandleActive : (hover ? _sliderHandleHover : _sliderHandle);
            GUI.Box(handleRect, GUIContent.none, hs);
            GUI.DrawTexture(new Rect(handleRect.x + handleD / 2f - Sc(2), handleRect.y + handleD / 2f - Sc(2), Sc(4), Sc(4)), Texture2D.whiteTexture);
            return nv;
        }

        // **R416-1**：提交入口。返回值与"上次写进配置的值"不同就写一次。
        // 这是 ImGui 的 `if (value != v_last)` 模式 —— 没有跨帧布尔、没有几何认领、没有帧号。
        private static bool SliderSubmit(ConfigEntryBase entry, float value) {
            if (entry == null) return false;
            float last;
            if (_sliderLastWritten.TryGetValue(entry, out last) && Mathf.Abs(last - value) < 0.0001f) return false;
            _sliderLastWritten[entry] = value;
            SetValue(entry, value);
            return true;
        }

        private static bool SliderRange(string key, out float min, out float max) {
            switch (key) {
                case "UI Scale": min = 1f; max = 1.8f; return true;
                case "Window Width": min = 400f; max = 1600f; return true;
                case "Window Height": min = 300f; max = 1000f; return true;
                case "Window X": min = 0f; max = 2000f; return true;
                case "Window Y": min = 0f; max = 2000f; return true;
                case "Sidebar Width": min = 0f; max = 320f; return true; //0 = 自动
            }
            min = 0f; max = 0f; return false;
        }

        // **走 SR 方法**：KeyboardShortcut 的显示格式（修饰键在前，如 "Ctrl + F"），与 SR 自己键位框一致
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

        // **R392**：带 isInternal 的版本（滑块路径要知道要不要画同行附加控件）
        private static void RenderControl(ConfigEntryBase entry, bool isInternal) {
            _curIsInternal = isInternal;
            object val = entry.BoxedValue;
            if (val is bool) {
                //复选框只负责开关（左键切换）；绑定快捷键统一走它前面的标签（见 RestoreLabel/RenderEntryRow）
                bool b = (bool)val;
                if (GUILayout.Button(b && _checkTex == null ? "✓" : "", b ? _checkOn : _checkOff, GUILayout.Width(CtlH()), GUILayout.Height(Sc(26)))) {
                    SetValue(entry, !b);
                }
                // **R411**：勾贴图叠画在复选框上（底色仍是淡蓝）。字形只在贴图缺失时兜底（见 SR.Styles）。
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
                //键位框本身也支持右键绑/解绑（与自绘开关/按钮一致）
                ConfigEntry<KeyCode> kcRow = entry as ConfigEntry<KeyCode>;
                if (kcRow != null) TryBindByRightClick(kcRow, GUILayoutUtility.GetLastRect());
            } else if (val is BepInEx.Configuration.KeyboardShortcut) {
                //外部模组（如 BetterFreeplay）的组合键用 KeyboardShortcut
                bool capturing = _capturing == entry;
                // **走 SR 方法**：外部插件的 KeyboardShortcut 也用"修饰键在前"的格式（原来是 val.ToString() -> F+Ctrl）
                string text = capturing ? RecText() : KbShortcutText((BepInEx.Configuration.KeyboardShortcut)val);
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(CtlH()))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; _recSeq.Clear(); _recHeld.Clear(); }
                }
            } else if (val is Enum) {
                Type et = val.GetType();
                string cur = EnumDisplayName(val.ToString());
                //枚举选项缓存（值不变；显示名每帧现算）, 外部模块页下拉框多，避免每帧 Enum.GetNames/GetValues
                object[] cachedVals;
                if (!_enumOptions.TryGetValue(entry, out cachedVals)) {
                    string[] names = Enum.GetNames(et);
                    Array vals = Enum.GetValues(et);
                    List<object> vlist = new List<object>();
                    for (int i = 0; i < names.Length; i++) {
                        object v = vals.GetValue(i);
                        //功能可注册"这个枚举项要不要保留"（如关卡下拉框过滤 空白/随机/原型）
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
                //下拉框宽度：默认 170，功能可注册更窄的宽度（外部模块页与同页编辑框等宽）
                float hookW = SR.RowComboWidth(entry);
                float cbw = hookW > 0f ? Sc(hookW) : Sc(170);
                int sel = ComboBox(entry, cur, cachedVals, dispNames, ref open, cbw);
                if (sel >= 0) SetValue(entry, cachedVals[sel]);
                _editOpen[entry] = open;
            } else if (val is int || val is float || val is string) {
                //功能可注册"这个数值条目用滑块渲染"（如 视野 FOV 1-32、外部模块的时间流速 0-2）；
                //拖动不会带动窗口（滑块取得 hot control）
                float hmin, hmax;
                string hfmt;
                if ((val is float || val is int) && SR.RowSliderRange(entry, out hmin, out hmax, out hfmt)) {
                    float fv = val is int ? (int)val : (float)val;
                    float nv = fv;
                    // **R414**：跨度大时把量化步长放大到"整条轨道至少 ~60 个可停点"，
                    // 否则 int 条目（大跨度）拖起来像没反应。float 条目不量化。
                    float hstep = 0f;
                    if (val is int) { float span = hmax - hmin; hstep = span > 4000f ? span / 60f : 1f; }
                    // **R415**：rect 提到 try 外面 —— 提交点在 EndHorizontal 之后要用它做几何认领
                    Rect sr = default(Rect);
                    GUILayout.BeginHorizontal();
                    try {
                        sr = GUILayoutUtility.GetRect(Sc(150), CtlH(), GUILayout.Width(SliderTrackWidth()));
                        nv = DrawSlider(sr, fv, hmin, hmax, val is int, hstep); //int 条目：整数步进（提交时的类型由 SetValue 归一化）
                        string __sv = nv.ToString(hfmt);   // **R411**：只格式化一次（原来标签文本与宽度各算一遍）
                        GUILayout.Label(__sv, _label, GUILayout.Width(SliderValueWidth(__sv)), GUILayout.Height(Sc(26)));
                        // R392：附加控件（快捷键框）画在**同一个水平组内**、紧跟数值标签,
                        //  用户要求"右边的快捷键框要挨着左边的滑块条"。
                        //  （原来它在 RenderEntryRow 的外层 BeginHorizontal 里，被滑块组的
                        //   EndHorizontal 断开，视觉上就跑到别处去了。）
                        RenderFollowUp(entry, _curIsInternal);
                    } finally { GUILayout.EndHorizontal(); }   // **R411**：成对兜底（R410-E1）
                    // **R416**：值变了就写（见 DrawSlider 处的说明）
                    SliderSubmit(entry, nv);
                    return;
                }
                //设置页数值条目也画成滑块（区间按设置项而定，见 SliderRange）
                float smin, smax;
                if ((val is int || val is float) && entry.Definition.Section == "Settings" && SliderRange(entry.Definition.Key, out smin, out smax)) {
                    bool isInt = val is int;
                    float fv = isInt ? (int)val : (float)val;
                    float nv = fv;
                    // **R414**：与上面同理，大跨度 int 条目按 ~60 个可停点量化
                    float sstep = 0f;
                    if (isInt) { float span = smax - smin; sstep = span > 4000f ? span / 60f : 1f; }
                    // **R415**：同上，rect 提到 try 外面（提交点要用它认领）
                    Rect sr = default(Rect);
                    GUILayout.BeginHorizontal();
                    try {
                        sr = GUILayoutUtility.GetRect(Sc(150), CtlH(), GUILayout.Width(SliderTrackWidth()));
                        nv = DrawSlider(sr, fv, smin, smax, isInt, sstep);
                        string __sv = nv.ToString(isInt ? "0" : "0.0");   // **R411**：只格式化一次
                        GUILayout.Label(__sv, _label, GUILayout.Width(SliderValueWidth(__sv)), GUILayout.Height(Sc(26)));
                    } finally { GUILayout.EndHorizontal(); }   // **R411**：成对兜底（R410-E1）
                    // **R416**：同上
                    SliderSubmit(entry, isInt ? Mathf.RoundToInt(nv) : nv);
                    return;
                }
                //界面语言：下拉框（内置 中文 / English + 已注册的外部语言包），运行时立即生效
                if (val is string && entry.Definition.Section == "Settings" && entry.Definition.Key == "Language") {
                    bool open;
                    if (!_editOpen.TryGetValue(entry, out open)) open = false;
                    //选项 = 内置两门 + 已注册的外部语言包（装几个就多几个）
                    string[] ids, names;
                    SR.LanguageOptions(out ids, out names);
                    string cur = (string)val;
                    int curIdx = Array.IndexOf(ids, cur);
                    if (curIdx < 0) curIdx = Array.IndexOf(ids, LangEn); //语言包被删了 -> 显示回退结果
                    if (curIdx < 0) curIdx = 0;
                    int sel = ComboBox(entry, names[curIdx], ids, names, ref open, Sc(120));
                    // ids 本身就是 SR 自己的语言 id 列表（见 SR.LanguageOptions），直接落值即可
                    if (sel >= 0 && sel < ids.Length) SetValue(entry, ids[sel]);
                    _editOpen[entry] = open;
                    return;
                }
                string txt;
                if (!_editText.TryGetValue(entry, out txt)) {
                    txt = Convert.ToString(val, CultureInfo.InvariantCulture);
                    _editText[entry] = txt;
                }
                //编辑框宽度：默认 170，功能可注册更窄的宽度（外部模块页与同页下拉框一致）
                float hookEditW = SR.RowComboWidth(entry);
                float editW = hookEditW > 0f ? Sc(hookEditW) : Sc(170);
                string ntxt = ClearableTextField(txt, _searchBox, GUILayout.Width(editW), GUILayout.Height(Sc(26)));
                if (ntxt != txt) {
                    _editText[entry] = ntxt;
                    _editSelfFrame = Time.frameCount;   // **R411**：标记"这是编辑框自己发起的改动"（见 SetValue）
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

        //下拉框（全 mod 唯一实现，行为统一）：按钮与展开列表放同一纵向组 -> 列表落在按钮正下方并对齐
        //（不再用 rect 相减估算缩进，避免列表与按钮脱节、被推出可视区）；列表项是普通 Button，不穿透。
        //展开当帧不画列表：IMGUI 控件矩形来自上一趟 Layout，当帧新建的控件没有 Layout 记录（矩形无效），
        //延后一帧绘制。注意 width：调用方传入的已是 Sc() 过的大小，这里不再二次缩放。
        // **按条目记录而不是一个全局帧号**：全局帧号会让"同帧内第二个下拉框"也被当成刚展开
        //  （drew=false -> 它的列表在这一帧消失，展开着的其它下拉框会闪一下）。
        private static readonly Dictionary<ConfigEntryBase, int> _comboSkipFrame = new Dictionary<ConfigEntryBase, int>();

        // **R411**：展开列表的滚动窗起点（按条目记，见 ComboBox 里"最多可见 8 项"）。
        private static readonly Dictionary<ConfigEntryBase, int> _comboScroll = new Dictionary<ConfigEntryBase, int>();
        // **R411**：编辑框自己提交配置的那一帧（见 SetValue：那一帧不清 _editText，避免吃掉小数点）
        private static int _editSelfFrame = -1;
        // **R414**：下拉框"点外面关闭"。不靠坐标（下拉框矩形在滚动区内容坐标系里，与 OnGUI 顶层的
        //  屏幕坐标对不上），而是约定：MouseDown 这一帧若没有任何下拉框自己认领（按钮或列表项被点到），
        //  就全部收起。认领由 ComboBox 在自己被点时打标（ComboMouseDownSeen）；收口在 ManagerUI.OnGUI 末尾。
        internal static int _comboSeenFrame = -1;
        internal static void ComboMouseDownSeen() { _comboSeenFrame = Time.frameCount; }
        internal static void CloseOpenCombos() {
            try {
                // 必须先拷贝键再改值：Dictionary 边枚举边写会让枚举器抛
                // "Collection was modified; enumeration operation may not execute"（用户 R413 实测）。
                // 哪怕只是改已有键的值也算写，该异常在 .NET 的 Dictionary 上同样会抛。
                ConfigEntryBase[] keys = new ConfigEntryBase[_editOpen.Count];
                _editOpen.Keys.CopyTo(keys, 0);
                for (int i = 0; i < keys.Length; i++) if (_editOpen[keys[i]]) _editOpen[keys[i]] = false;
            } catch (Exception __ex) { Guard.Log("收起展开的下拉框", __ex); }
        }

        // 悬停在下拉框上滚轮 = 换选项（不滚页面）。
        // 滚轮所有权借鉴 Dear ImGui：想独占滚轮的控件在自己被 hover 时声明一下，帧首把"本帧声明"转存成"上一帧声明"，
        // 滚动仲裁读上一帧声明、有就整体放弃滚动。只能读上一帧，是因为滚动容器的处理发生在控件提交之前（IMGUI 结构决定的）。
        // 关键是由控件当帧自己声明，而不是滚动区拿缓存矩形去猜（旧的矩形缓存机制已经删了）。
        private static bool _wheelClaimCur;
        private static bool _wheelClaimPrev;
        private static int _wheelClaimFrame = -1;

        private static float SliderValueWidth(string text) {
            try {
                return Mathf.Max(Sc(44), _label.CalcSize(new GUIContent(text)).x + Sc(8));
            } catch { return Sc(44); }
        }
        // 悬停在下拉框上滚轮 = 换选项（不滚页面）。
        // 滚轮所有权借鉴 Dear ImGui：想独占滚轮的控件在自己被 hover 时声明一下，帧首把"本帧声明"转存成"上一帧声明"，
        // 滚动仲裁读上一帧声明、有就整体放弃滚动。只能读上一帧，是因为滚动容器的处理发生在控件提交之前（IMGUI 结构决定的）。
        // 关键是由控件当帧自己声明，而不是滚动区拿缓存矩形去猜（旧的矩形缓存机制已经删了）。
        // 中心化 hover 裁决（借鉴 ItemHoverable, imgui.cpp:3501）
        //ImGui 规则：一帧之内只有**第一个**判定通过的控件算 hovered（g.HoveredId != 0 即拒绝后来者），
        //叠放控件必须显式 AllowOverlap。这样"谁是 hovered"由中心裁决，而不是每个控件各自 rect.Contains。
        // **加法式改造**：目前只有下拉框走新路径，其余控件保持原判定（无复现症状时不做大改）。
        private static int _hoverFrame = -1;
        private static bool _hoverTaken;
        //返回本控件是否**赢得**本帧 hover；allowOverlap = true 时无视先到先得（叠放控件用）
        internal static bool HoverTest(Rect r, bool allowOverlap = false) {
            try {
                Event e = Event.current;
                if (e == null) return false;
                if (_hoverFrame != Time.frameCount) { _hoverFrame = Time.frameCount; _hoverTaken = false; }
                if (!r.Contains(e.mousePosition)) return false;
                if (!allowOverlap && _hoverTaken) return false;   // **先到先得**
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
        //控件高度（设计像素）：0 = 默认 26；功能可覆盖自己那一页的编辑框/下拉框/复选框高度
        public static float ControlHeight;
        //Area 偏移（窗口外框内的内容区原点），供"事件坐标 <-> 内容坐标"换算
        internal static Vector2 AreaOffset;
        internal static bool WheelShieldHit;
        //本帧待用的滚轮量：由滚动区在 ScrollWheel 趟用 WheelDeltaSet 记下，控件用 WheelDeltaTake 取用。
        // 取用即清, 不再有"帧号 + 手工清零"那套（原来只写不清，导致滑块一悬浮就被打到最小值）。
        private static float _wheelDelta;
        internal static void WheelDeltaSet(float d) { try { _wheelDelta = d; } catch (Exception __ex) { Guard.Log("WheelDeltaSet", __ex); } }
        internal static float WheelDeltaTake() { try { float d = _wheelDelta; _wheelDelta = 0f; return d; } catch { return 0f; } }
        internal static void ResetWheelState() {
            try {
                _wheelClaimPrev = false; _wheelClaimCur = false; _wheelClaimFrame = -1;
                WheelShieldHit = false; _wheelDelta = 0f;
                _sliderLastWritten.Clear();   // **R416**：清"已写入值"表（跨场景后配置可能已变）
            } catch (Exception __ex) { Guard.Log("ResetWheelState", __ex); }
        }

        //控件高度：默认 26，功能可用 SR.Ctl.ControlHeight 覆盖（EX = 30）
        private static float CtlH() { try { return Sc(ControlHeight > 0f ? ControlHeight : 26f); } catch { return Sc(26); } }

        // 滑块轨道的**固定**宽度（像素）。
        //  GUILayoutUtility.GetRect(w, h) 不带 ExpandWidth 时，轨道实际宽度会被父级水平布局按
        //  "剩余空间"分配 -> 窗口改宽/侧栏改动/UI 缩放一变，滑块就跟着忽长忽短（用户反馈的现象）。
        //  显式给 GUILayout.Width 才锁定。这里仍是 Sc(...)：随 UI 缩放缩放，但**不随窗口宽度变**。
        public static float SliderTrackWidth() { try { return Sc(150); } catch { return 150f; } }

        private static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = Sc(170);
            int clicked = -1;
            //"本帧是否展开"用进入时的状态决定：保证本帧 Layout 与 Repaint 的控件结构一致
            int skipFrame;
            bool drew = open && !(entry != null && _comboSkipFrame.TryGetValue(entry, out skipFrame) && skipFrame == Time.frameCount);
            GUILayout.BeginVertical(GUILayout.Width(width));
            if (GUILayout.Button(current + "   v", _frame, GUILayout.Width(width), GUILayout.Height(CtlH()))) {
                ComboMouseDownSeen();   // **R411**：本下拉框自己被点了（"点外面关闭"的判定用）
                open = !open;
                if (entry != null) _editOpen[entry] = open;   // **P2 null 保护**：Dictionary 用 null 作键会抛 ArgumentNullException -> 崩掉整个 OnGUI
                if (entry != null) _comboSkipFrame[entry] = Time.frameCount; //展开当帧不画列表，下一帧起
            }
            //悬停滚轮换选项：记录收起状态的矩形（供滚动区之前吞事件），并在 Repaint 阶段落值
            //（Repaint 是每帧最后一次绘制，此时改配置不会打断本帧 Layout；下拉框宽度固定，也不会引起布局变化）
            Rect boxRect = GUILayoutUtility.GetLastRect();
            // **ImGui 式所有权**：控件自己在被 hover 时声明要滚轮
            Event ce0 = Event.current;
            if (ce0 != null && options != null) {
                Rect listRect = new Rect(boxRect.x, boxRect.yMax, width, Sc(26) * options.Length);
                //中心裁决：框命中即赢；否则展开的列表用 AllowOverlap 抢下 hover（它叠放在下方各行之上）
                bool won = HoverTest(boxRect) || (drew && HoverTest(listRect, true));
                if (won) WheelClaim();   //只有赢得 hover 才声明滚轮 -> 一帧内不会有两个控件同时声明
            }
            // 滚轮换选项只在**收起**时生效：展开时滚轮留给展开列表与页面滚动（否则点开一个下拉框
            //  想滚列表，结果选项先被滚轮换掉了）。下面 WheelClaim 才是"无论开合都登记"的那部分。
            Event ce = Event.current;
            if (!open && ce != null && ce.type == EventType.Repaint && options != null && options.Length > 1
                && boxRect.Contains(ce.mousePosition)) {
                                // **#4 真因**：这里原来只读 Input.GetAxis("Mouse ScrollWheel")，该环境下恒为 0
                //  -> 滚轮换选项永远不生效（页面会滚，说明 ScrollWheel 事件本身是到达的）。
                //  改用滚轮盾在事件里记下的量（帧内有效、取用即清）。
                float cw = WheelDeltaTake();
                if (Mathf.Abs(cw) > 0.0001f) {
                    int cur = 0;
                    for (int i = 0; i < options.Length; i++) { if (options[i] == current) { cur = i; break; } }
                    int ni = cw > 0f ? cur - 1 : cur + 1; //上滚 = 上一项
                    if (ni < 0) ni = options.Length - 1;
                    if (ni >= options.Length) ni = 0;
                    clicked = ni;
                    SR.MarkWheelUsed();   //滚轮已给下拉框换选项：地图/自由相机别再缩放
                }
            }
            // **R411**：展开状态下滚轮 = 滚列表（上面那段只管收起态）。
            //  WheelClaim 已在本框/展开列表上登记过（HoverTest 那段），滚动区不会抢 -> 这里放心取用。
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
                // **R411**：展开列表最多可见 8 项（对齐 ImGui 的 HeightRegular），其余滚轮滚。
                //  原来有多少项就铺多少 -> 关卡下拉上百项时会把整页顶出去、还会被滚动区裁掉。
                int __visN = Mathf.Min(options.Length, 8);
                int __first = 0;
                if (options.Length > __visN && entry != null) {
                    int __cur = -1;
                    for (int i = 0; i < options.Length; i++) { if (options[i] == current) { __cur = i; break; } }
                    int __sc;
                    if (!_comboScroll.TryGetValue(entry, out __sc)) __sc = 0;
                    if (__cur >= 0) {
                        if (__cur < __sc) __sc = __cur;                              //选中项在窗口上方 -> 跟上去
                        else if (__cur >= __sc + __visN) __sc = __cur - __visN + 1;  //在下方 -> 贴底
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
                ComboMouseDownSeen();   // **R411**：列表项被点（"点外面关闭"的判定用）
                open = false;
                _editOpen[entry] = false;
            }
            return clicked; //调用方按索引取 value 并落配置
        }

    }
}
