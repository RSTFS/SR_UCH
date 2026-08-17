using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class ModManager {

// ==== 分区：Settings（设置页 / 通用条目行渲染 / 控件渲染 / 滑块 / 下拉框）====

        private static void SetValue(ConfigEntryBase entry, object value) {
            try {
                //进度解锁：未达标时锁定项强制保持 false（控制台勾选/快捷键都不可开启）
                if (value is bool b && b && ProgressionLocked(entry)) {
                    MainPlugin.ModLogger.LogInfo("[进度解锁] 未达标，保持禁用: " + entry.Definition.Section + "/" + entry.Definition.Key);
                    return;
                }
                entry.BoxedValue = value;
                if (entry == _blockInputEntry) {
                    BlockInput = value is bool bv && bv;
                    ApplyEventSystemGate();
                }
                _dirtyConfig = entry.ConfigFile;
                _dirty = true;
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("配置修改失败: " + ex.Message);
            }
        }

        private static List<ConfigEntryBase> InternalSectionEntries() {
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            if (_internalConfig == null) return result;
            string q = _search.Trim().ToLower();
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section != _selectedInternalSection) continue;
                if (q.Length > 0 && !e.Definition.Key.ToLower().Contains(q)) continue;
                //追踪玩家仅在列表模式=普通时显示（进阶时隐藏）
                if (e.Definition.Section == "Destroy Blocks" && e.Definition.Key == "Track Player"
                    && !DestroyBlocks.TrackPlayerVisible) continue;
                result.Add(e);
            }
            return result;
        }

        //设置 page entries grouped by category (visual separation between unrelated items);
        //order matches the config bind order, 插件 stays last
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
            foreach (ConfigEntryBase entry in SettingsEntries()) {
                any = true;
                string g = SettingsGroup(entry.Definition.Key);
                if (g != null && g != lastGroup) {
                    lastGroup = g;
                    GUILayout.Label("— " + T(g, g == "按键" ? "Keys" : g == "界面" ? "UI" : "Plugins") + " —", _secHeader);
                }
                RenderEntryRow(entry, true, colWidth);
            }
            if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
        }

        private static List<ConfigEntryBase> SettingsEntries() {
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            if (_internalConfig == null) return result;
            string q = _search.Trim().ToLower();
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section != "设置") continue;
                //「过滤快捷消息」「隐藏聊天窗口」在会话内容页有专用开关、「All Enabled」在搜索栏右侧有专用总开关按钮，
                //设置页不再重复渲染成通用复选框（避免冗余不美观）。
                if (e.Definition.Key == "过滤快捷消息" || e.Definition.Key == "隐藏聊天窗口" || e.Definition.Key == "All Enabled") continue;
                if (q.Length > 0 && !e.Definition.Key.ToLower().Contains(q)) continue;
                result.Add(e);
            }
            return result;
        }

        //the 地图 page: only the map key rebind (the map itself opens with the M key)
        //可点击的条目标签：点击恢复该条目的默认值（自定义按键/数值/滑块都适用）。
        //非开关条目（按键/数值/枚举/文本）的默认值显示在悬浮提示里（不占标签文字），如“默认: 20”。
        private static void RestoreLabel(GUIContent content, ConfigEntryBase entry, float w, float h) {
            GUIContent c2 = content;
            try {
                if (entry != null && !(entry.BoxedValue is bool)) {
                    string dft = FormatDefaultValue(entry.DefaultValue);
                    if (!string.IsNullOrEmpty(dft)) {
                        string tip = content.tooltip != null ? content.tooltip : "";
                        if (tip.IndexOf("默认", StringComparison.Ordinal) < 0) {
                            tip += (tip.Length > 0 ? "\n" : "") + T("默认: " + dft, "Default: " + dft);
                        }
                        c2 = new GUIContent(content.text, tip);
                    }
                }
            } catch { }
            if (GUILayout.Button(c2, _label, GUILayout.Width(w), GUILayout.Height(h))) {
                try { entry.BoxedValue = entry.DefaultValue; } catch { }
                _editText.Remove(entry);
                _editOpen.Remove(entry);
                if (_capturing == entry) _capturing = null;
            }
        }

        //把配置默认值格式化成简短文本（按键显示键名、枚举显示中文名、数值原样）
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


        private static List<ConfigEntryBase> VisibleEntries(ConfigFile config) {
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            if (config == null) return result;
            string q = _search.Trim().ToLower();
            foreach (ConfigEntryBase e in AllEntries(config)) {
                if (q.Length > 0 && !e.Definition.Key.ToLower().Contains(q)) continue;
                result.Add(e);
            }
            return result;
        }

        //all config entries (BepInEx 5.4: GetConfigEntries is obsolete; the IDictionary
        //interface exposes Values without the protected Entries property)
        private static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) {
            var dict = (System.Collections.Generic.IDictionary<BepInEx.Configuration.ConfigDefinition, ConfigEntryBase>)config;
            return dict.Values;
        }

        private static float SidebarWidth() {
            float maxW = _label.CalcSize(new GUIContent(T("内部", "Internal"))).x;
            foreach (string s in _internalSections) {
                if (s == "EX" && !ExRef.Loaded) continue; //未安装附加模块：不参与宽度计算
                maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(ZhSection(s))).x);
            }
            foreach (PluginEntry p in _externalPlugins) {
                maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(p.name)).x);
            }
            return Mathf.Clamp(maxW + 26f, 110f, 260f);
        }

        private static float EntryNameWidth() {
            //缓存：模式/分区/语言/缩放/搜索/外部插件不变时列宽不变，避免每帧对所有条目名 CalcSize
            string key = _mode + "|" + _selectedInternalSection + "|" + _langEn + "|" + _scaled + "|" + _search
                + "|" + (CurrentExternalPlugin() != null ? CurrentExternalPlugin().guid : "");
            if (key == _nameWKey) return _nameWCached;
            _nameWKey = key;
            float maxW = 60f;
            if (_mode == Mode.Internal) {
                foreach (ConfigEntryBase e in InternalSectionEntries()) maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(ZhKey(e))).x);
            } else if (_mode == Mode.Settings) {
                foreach (ConfigEntryBase e in SettingsEntries()) maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(ZhKey(e))).x);
            } else {
                ConfigFile cfg = CurrentExternalPlugin() != null ? CurrentExternalPlugin().config : null;
                foreach (ConfigEntryBase e in VisibleEntries(cfg)) maxW = Mathf.Max(maxW, _label.CalcSize(new GUIContent(e.Definition.Key)).x);
            }
            _nameWCached = Mathf.Clamp(maxW + 16f, 90f, 240f);
            return _nameWCached;
        }

        private static bool IsBuilderToggleKey(string key) {
            return key == "Collision Toggle Key";
        }

        //find one internal config entry by section + key (used to pair override rows)
        private static ConfigEntryBase FindInternalEntry(string section, string key) {
            if (_internalConfig == null) return null;
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section == section && e.Definition.Key == key) return e;
            }
            return null;
        }

        //format a config entry's default value for the tooltip (bool/int/float/string/enum/key)
        private static string FormatDefaultValue(ConfigEntryBase entry) {
            try {
                if (entry == null || entry.DefaultValue == null) return "";
                object dv = entry.DefaultValue;
                if (dv is bool) return ((bool)dv) ? (_langEn ? "ON" : "开") : (_langEn ? "OFF" : "关");
                if (dv is float) return ((float)dv).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                if (dv is int) return ((int)dv).ToString();
                if (dv is System.Enum) return EnumDisplayName(dv.ToString());
                if (dv is KeyCode) return KeyDisplayName((KeyCode)dv);
                return Convert.ToString(dv, System.Globalization.CultureInfo.InvariantCulture);
            } catch { return ""; }
        }

        private static void RenderEntryRow(ConfigEntryBase entry, bool isInternal, float colWidth) {
            string name = isInternal ? ZhKey(entry) : entry.Definition.Key;
            //描述：中文模式用 ZhDesc/配置描述；英文模式只用 ZhDesc 的英文表（查不到留空，不显示中文）
            string desc;
            if (_langEn && !_forceZh) {
                desc = isInternal ? ZhDesc(entry) : null;
                if (desc == null) desc = "";
            } else {
                string zhDesc = isInternal ? ZhDesc(entry) : null;
                desc = zhDesc != null ? zhDesc
                    : (entry.Description != null && entry.Description.Description != null ? entry.Description.Description : "");
            }
            //adapt the name column to the space actually available, so rows never overflow
            //(Builder rows also carry a checkbox + a key box; plain rows only one control)
            bool paired = isInternal && entry.Definition.Section == "Builder Enhancements" &&
                entry.Definition.Key == "Collision Override";
            float avail = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(24));
            float nameW = Mathf.Clamp(colWidth, Sc(50), Mathf.Max(Sc(50), avail - (paired ? Sc(215) : Sc(185))));
            //name column: wrapped in a vertical group so the label gets its FULL height
            //(a label directly inside BeginHorizontal only gets single-line height and
            //the CJK glyph sink / wrapped lines get clipped)
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(nameW));
            //name column: clickable to restore the default value; tooltip shows the default
            string defText = FormatDefaultValue(entry);
            string tip = desc;
            if (ProgressionLocked(entry)) {
                bool groupA = entry.Definition.Section == "Builder Enhancements";
                tip += groupA
                    ? "\n🔒 " + T("A组未解锁：需游戏时长 > 17时16分18秒 或 奔跑长度 > 52000米（实验页查看进度）", "Group A locked: need >17h16m18s playtime or >52000m run distance (see Experiments page)")
                    : "\n🔒 " + T("B组未解锁：需游戏时长 > 52时 或 奔跑长度 > 100000米（实验页查看进度）", "Group B locked: need >52h playtime or >100000m run distance (see Experiments page)");
            }
            if (defText.Length > 0) {
                tip += "\n" + T("默认: " + defText, "Default: " + defText);
            }
            tip += "\n" + T("点击恢复默认值", "Click to reset to default");
            if (GUILayout.Button(new GUIContent(name, tip),
                _nameLabel, GUILayout.Width(nameW), GUILayout.Height(TextHeight(name, nameW)))) {
                try { entry.BoxedValue = entry.DefaultValue; } catch { }
                _editText.Remove(entry);
                _editOpen.Remove(entry);
            }
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            RenderControl(entry);
            //建造增强 rows: 名称 | 选择框 | 快捷键框 on one line (override + its toggle key)
            if (isInternal && entry.Definition.Section == "Builder Enhancements") {
                string pair = null;
                if (entry.Definition.Key == "Collision Override") pair = "Collision Toggle Key";
                if (pair != null) {
                    GUILayout.Space(Sc(8));
                    ConfigEntryBase k = FindInternalEntry("Builder Enhancements", pair);
                    if (k != null) RenderControl(k);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
        }

        //measured text height + extra room for the CJK glyph sink, so labels never clip
        //（按 文本+宽度+缩放 缓存，条目名固定时不再每帧 CalcHeight）
        private static float TextHeight(string text, float width) {
            try {
                string key = text + "|" + width + "|" + _scaled;
                float v;
                if (_heightCache.TryGetValue(key, out v)) return v;
                v = Mathf.Max(Sc(32), _nameLabel.CalcHeight(new GUIContent(text), width) + Sc(16));
                if (_heightCache.Count > 800) _heightCache.Clear(); //防膨胀
                _heightCache[key] = v;
                return v;
            } catch {
                return Sc(32);
            }
        }

        //Dear ImGui 风格水平滑块（SliderFloat 范式）：
        //  - 细轨道 + 已填充段（蓝）+ 圆形把手（normal/hover/active 三态反馈）
        //  - 左键拖拽把手或点击轨道直接跳转；悬停/拖拽时滚轮微调
        //  - 返回新值（调用方比较变化后 SetValue）
        private static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false) {
            float t = Mathf.InverseLerp(min, max, value);
            float trackH = Sc(4), handleD = Sc(16);
            Rect trackRect = new Rect(rect.x, rect.y + (rect.height - trackH) / 2f, rect.width, trackH);
            Rect fillRect = new Rect(rect.x, trackRect.y, rect.width * t, trackH);
            Rect handleRect = new Rect(rect.x + (rect.width - handleD) * t, rect.y + (rect.height - handleD) / 2f, handleD, handleD);
            Event ev = Event.current;
            int id = GUIUtility.GetControlID(14001, FocusType.Passive, rect);
            bool hover = rect.Contains(ev.mousePosition);
            bool dragging = GUIUtility.hotControl == id;
            if (ev.type == EventType.MouseDown && ev.button == 0 && rect.Contains(ev.mousePosition)) {
                GUIUtility.hotControl = id;
                dragging = true;
                t = Mathf.InverseLerp(rect.x, rect.xMax, ev.mousePosition.x);
                ev.Use();
            } else if (ev.type == EventType.MouseDrag && dragging && rect.Contains(ev.mousePosition)) {
                t = Mathf.InverseLerp(rect.x, rect.xMax, ev.mousePosition.x);
                ev.Use();
            } else if (ev.type == EventType.MouseUp && dragging) {
                GUIUtility.hotControl = 0;
                dragging = false;
                ev.Use();
            } else if (ev.type == EventType.ScrollWheel && hover && !dragging) {
                float step = intStep ? 1f : Mathf.Max((max - min) / 100f, 0.01f);
                value = Mathf.Clamp(value + (ev.delta.y > 0f ? -step : step) * (intStep ? 1f : 5f), min, max);
                ev.Use();
                return value;
            }
            //绘制（Repaint 或任意帧都画，事件帧提前画把手以命中 hover）
            GUI.Box(trackRect, GUIContent.none, _sliderTrack);
            if (fillRect.width > 0.5f) GUI.Box(fillRect, GUIContent.none, _sliderFill);
            GUIStyle hs = dragging ? _sliderHandleActive : (hover ? _sliderHandleHover : _sliderHandle);
            GUI.Box(handleRect, GUIContent.none, hs);
            //把手中心点画个小圆点（Dear ImGui 把手细节）
            GUI.DrawTexture(new Rect(handleRect.x + handleD / 2f - Sc(2), handleRect.y + handleD / 2f - Sc(2), Sc(4), Sc(4)), Texture2D.whiteTexture);
            float nv = Mathf.Lerp(min, max, Mathf.Clamp01(t));
            if (intStep) nv = Mathf.Round(nv);
            return Mathf.Clamp(nv, min, max);
        }

        //slider ranges for 设置 page numeric entries
        private static bool SliderRange(string key, out float min, out float max) {
            switch (key) {
                case "UI Scale": min = 1f; max = 1.8f; return true;
                case "Window Width": min = 400f; max = 1200f; return true;
                case "Window Height": min = 300f; max = 1000f; return true;
                case "Window X": min = 0f; max = 2000f; return true;
                case "Window Y": min = 0f; max = 2000f; return true;
            }
            min = 0f; max = 0f; return false;
        }

        private static void RenderControl(ConfigEntryBase entry) {
            object val = entry.BoxedValue;
            if (val is bool) {
                bool b = (bool)val;
                //进度解锁：未达标时强制禁用（灰显），达标后解锁可手动开启
                bool locked = ProgressionLocked(entry);
                bool oldEn = GUI.enabled;
                if (locked) GUI.enabled = false;
                if (GUILayout.Button(b ? "✓" : "", b ? _checkOn : _checkOff, GUILayout.Width(Sc(26)), GUILayout.Height(Sc(26)))) {
                    SetValue(entry, !b);
                }
                GUI.enabled = oldEn;
            } else if (val is KeyCode) {
                bool capturing = _capturing == entry;
                string text;
                if (capturing) {
                    text = T("请按键... (Esc 清空)", "Press a key... (Esc=clear)");
                } else {
                    KeyCode kc = (KeyCode)val;
                    //组合键显示：Shift/Ctrl/Alt + 主键（捕捉时按住修饰键即可设置组合）
                    text = ComboKeyDisplay(entry, kc);
                }
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(26)))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; }
                }
            } else if (val is BepInEx.Configuration.KeyboardShortcut) {
                //combo keys (external mods like BetterFreeplay use KeyboardShortcut)
                bool capturing = _capturing == entry;
                string text = capturing ? T("请按键... (Esc 清空)", "Press a key... (Esc=clear)") : val.ToString();
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(26)))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; }
                }
            } else if (val is Enum) {
                Type et = val.GetType();
                string[] names = Enum.GetNames(et);
                Array vals = Enum.GetValues(et);
                string cur = EnumDisplayName(val.ToString());
                //问号关卡 / EX 指定关卡下拉框：过滤掉不合适的关卡（空白/随机/原型 PROTOTYPE1-8），避免误选
                bool qLevel = (entry.Definition.Section == "实验" && entry.Definition.Key == "Question Level")
                    || (entry.Definition.Section == "EX" && entry.Definition.Key == "Target Level");
                List<string> dispList = new List<string>();
                List<object> valList = new List<object>();
                for (int i = 0; i < names.Length; i++) {
                    object v = vals.GetValue(i);
                    if (qLevel) {
                        if (names[i] == "BLANKLEVEL" || Convert.ToInt32(v) >= (int)GameState.LevelName.RANDOM) continue;
                    }
                    dispList.Add(EnumDisplayName(names[i]));
                    valList.Add(v);
                }
                string[] dispNames = dispList.ToArray();
                object[] fvals = valList.ToArray();
                bool open;
                if (!_editOpen.TryGetValue(entry, out open)) open = false;
                //下拉框宽度与同页编辑框一致（EX 页窄：与金额/数量输入框同宽；普通页 170）
                float cbw = entry.Definition.Section == "EX" ? Sc(80) : Sc(170);
                int sel = ComboBox(entry, cur, fvals, dispNames, ref open, cbw);
                if (sel >= 0) {
                    SetValue(entry, fvals[sel]);
                    _editOpen[entry] = false;
                } else {
                    _editOpen[entry] = open;
                }
            } else if (val is int || val is float || val is string) {
                //视野 FOV slider (1-20); dragging never moves the window (slider grabs hot control)
                if (val is float && entry.Definition.Section == "视野" && entry.Definition.Key == "FOV") {
                    float fv = (float)val;
                    GUILayout.BeginHorizontal();
                    Rect sr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                    float nv = DrawSlider(sr, fv, 1f, 20f);
                    GUILayout.Label(nv.ToString("0"), _label, GUILayout.Width(Sc(44)), GUILayout.Height(Sc(26)));
                    GUILayout.EndHorizontal();
                    if (Mathf.Abs(nv - fv) > 0.001f) SetValue(entry, nv);
                    return;
                }
                //附加 Time Scale slider (0 = pause, 2 = fast)
                if (val is float && entry.Definition.Section == "EX" && entry.Definition.Key == "Time Scale") {
                    float fv = (float)val;
                    GUILayout.BeginHorizontal();
                    Rect sr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                    float nv = DrawSlider(sr, fv, 0f, 2f);
                    GUILayout.Label(nv.ToString("0.0"), _label, GUILayout.Width(Sc(44)), GUILayout.Height(Sc(26)));
                    GUILayout.EndHorizontal();
                    if (Mathf.Abs(nv - fv) > 0.001f) SetValue(entry, nv);
                    return;
                }
                //实验 Sync Frequency slider (10 - 30 Hz, integer steps)
                if (val is int && entry.Definition.Section == "实验" && entry.Definition.Key == "Sync Frequency") {
                    int iv = (int)val;
                    GUILayout.BeginHorizontal();
                    Rect sr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                    float nv = DrawSlider(sr, iv, 10f, 50f, true);
                    GUILayout.Label(Mathf.RoundToInt(nv) + " Hz", _label, GUILayout.Width(Sc(54)), GUILayout.Height(Sc(26)));
                    GUILayout.EndHorizontal();
                    if (Mathf.Abs(nv - iv) > 0.001f) SetValue(entry, Mathf.RoundToInt(nv));
                    return;
                }
                //实验 Score Discount：编辑框直接输入整数（0-100，任意值如 85；走下方通用数字编辑框）
                //设置 page numeric entries become sliders too (ranges chosen per setting)
                float smin, smax;
                if ((val is int || val is float) && entry.Definition.Section == "设置" && SliderRange(entry.Definition.Key, out smin, out smax)) {
                    bool isInt = val is int;
                    float fv = isInt ? (int)val : (float)val;
                    GUILayout.BeginHorizontal();
                    Rect sr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                    float nv = DrawSlider(sr, fv, smin, smax, isInt);
                    GUILayout.Label(nv.ToString(isInt ? "0" : "0.0"), _label, GUILayout.Width(Sc(44)), GUILayout.Height(Sc(26)));
                    GUILayout.EndHorizontal();
                    if (Mathf.Abs(nv - fv) > 0.001f) SetValue(entry, isInt ? Mathf.RoundToInt(nv) : nv);
                    return;
                }
                //界面语言：下拉框（中文 / English），运行时立即生效
                if (val is string && entry.Definition.Section == "设置" && entry.Definition.Key == "Language") {
                    bool open;
                    if (!_editOpen.TryGetValue(entry, out open)) open = false;
                    int sel = ComboBox(entry, (string)val, new[] { "中文", "English" }, new[] { "中文", "English" }, ref open, Sc(120));
                    if (sel >= 0) {
                        SetValue(entry, sel == 0 ? "中文" : "English");
                        _editOpen[entry] = false;
                    } else {
                        _editOpen[entry] = open;
                    }
                    return;
                }
                string txt;
                if (!_editText.TryGetValue(entry, out txt)) {
                    txt = Convert.ToString(val, CultureInfo.InvariantCulture);
                    _editText[entry] = txt;
                }
                //附加页的金额/编号框窄一点（加生命/加金币/方块编号/分数数量）
                float editW = Sc(170);
                if (entry.Definition.Section == "EX" &&
                    (entry.Definition.Key == "Lives Amount" ||
                     entry.Definition.Key == "Coin Amount" || entry.Definition.Key == "Piece Index")) {
                    editW = Sc(80);
                }
                string ntxt = GUILayout.TextField(txt, _searchBox, GUILayout.Width(editW), GUILayout.Height(Sc(26)));
                if (ntxt != txt) {
                    _editText[entry] = ntxt;
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

        //下拉框（照 SR_OLD 内联展开式）：按钮点击后在布局内直接展开选项列表。
        //不弹层、不覆盖：列表项是普通按钮（点击天然命中，不会穿透），滚轮由外层
        //ScrollView 处理，无坐标换算问题——比自绘弹层方案稳定得多。
        private static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = Sc(170);
            int sel = -1;
            if (GUILayout.Button(current + "   ▾", _frame, GUILayout.Width(Sc(width)), GUILayout.Height(Sc(26)))) {
                open = !open;
            }
            if (open) {
                //展开列表（内联，占布局位置）
                GUILayout.BeginVertical(_popup, GUILayout.Width(Sc(width)));
                for (int i = 0; i < options.Length; i++) {
                    bool isSel = options[i] == current;
                    if (GUILayout.Button(options[i], isSel ? _selItem : _item, GUILayout.Height(Sc(26)), GUILayout.ExpandWidth(true))) {
                        sel = i;
                        open = false;
                    }
                }
                GUILayout.EndVertical();
            }
            return sel;
        }

	}
}
