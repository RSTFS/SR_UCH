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
public partial class SR {

// ==== 分区：Settings（设置页 / 通用条目行渲染 / 控件渲染 / 滑块 / 下拉框）====

        //枚举下拉选项缓存（值不变；显示名按语言每帧现算）——避免每帧 GetNames/GetValues/遍历
        private static readonly Dictionary<Type, object[]> _enumOptions = new Dictionary<Type, object[]>();

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
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section != _selectedInternalSection) continue;
                //追踪玩家仅在列表模式=普通时显示（进阶时隐藏
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
                //「栏目宽度」专用行：滑块 + [−]/[+] 步进按钮 + 当前值
                //按钮是绝对可靠的点击路径；即使滑块在本机某些情况下失效，+/− 也能调
                if (entry.Definition.Key == "Sidebar Width") { RenderSidebarWidthRow(); continue; }
                string g = SettingsGroup(entry.Definition.Key);
                if (g != null && g != lastGroup) {
                    lastGroup = g;
                    GUILayout.Label("— " + T(g, g == "按键" ? "Keys" : g == "界面" ? "UI" : "Plugins") + " —", _secHeader);
                }
                RenderEntryRow(entry, true, colWidth);
            }
            if (!any) GUILayout.Label(T("（无匹配条目）", "(no matching entries)"), _label);
        }

        //「栏目宽度」专用行：滑块 + [−]/[+] 步进按钮 + 当前值（0 = 自动
        private static void RenderSidebarWidthRow() {
            int cur = _sidebarWEntry != null ? Mathf.Clamp(_sidebarWEntry.Value, 0, 320) : 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(T("栏目宽度", "Sidebar Width"), T("左侧栏目栏宽度（0 = 自动，100-320）", "Left sidebar width (0 = auto, 100-320)")), _label, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(26)));
            if (GUILayout.Button("−", _btn, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) SetSidebarWidth(cur - 16);
            Rect sr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
            float nv = DrawSlider(sr, cur, 0f, 320f, true);
            if (_sliderCommitted && Mathf.Abs(nv - cur) > 0.001f) SetSidebarWidth(Mathf.RoundToInt(nv));
            if (GUILayout.Button("+", _btn, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) SetSidebarWidth(cur + 16);
            GUILayout.Label(cur + " px", _label, GUILayout.Width(Sc(70)), GUILayout.Height(Sc(26)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        //设置栏目宽度：同时更新即时值（立刻改变布局）与配置项（持久化）
        private static void SetSidebarWidth(int px) {
            px = Mathf.Clamp(px, 0, 320);
            _sidebarW = px;
            if (_sidebarWEntry != null) _sidebarWEntry.Value = px;
        }

        private static List<ConfigEntryBase> SettingsEntries() {
            List<ConfigEntryBase> result = new List<ConfigEntryBase>();
            if (_internalConfig == null) return result;
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section != "设置") continue;
                //「过滤快捷消息」「隐藏聊天窗口」「显示时间」在会话内容页有专用开关、「All Enabled」在搜索栏右侧有专用总开关按钮，
                //设置页不再重复渲染成通用控件（避免重复，用户反馈"显示时间"与绘画/会话界面重复）
                if (e.Definition.Key == "过滤快捷消息" || e.Definition.Key == "隐藏聊天窗口"
                    || e.Definition.Key == "显示时间" || e.Definition.Key == "All Enabled") continue;
                //「组合键 XXX」是键位修饰键的内部持久化条目（RegisterComboEntry 自动建立），
                //不该作为普通设置显示在设置页里
                if (e.Definition.Key.StartsWith("组合键 ")) continue;
                result.Add(e);
            }
            return result;
        }

        //the 地图 page: only the map key rebind (the map itself opens with the M key)
        //可点击的条目标签：点击恢复该条目的默认值（自定义按键/数值/滑块都适用）
        //非开关条目（按键/数值/枚举/文本）的默认值显示在悬浮提示里（不占标签文字），如“默认: 20”
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
            } catch (Exception __ex) { Guard.Log("条目默认值处理", __ex); }
            if (GUILayout.Button(c2, _label, GUILayout.Width(w), GUILayout.Height(h))) {
                try { entry.BoxedValue = entry.DefaultValue; } catch (Exception __ex) { Guard.Log("恢复条目默认值", __ex); }
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
            foreach (ConfigEntryBase e in AllEntries(config)) {
                result.Add(e);
            }
            return result;
        }

        //all config entries (BepInEx 5.4: GetConfigEntries is public; the private
        //Entries getter is patched empty for third-party managers — SR_UCH reads here)
        private static IEnumerable<ConfigEntryBase> AllEntries(ConfigFile config) {
            return config != null ? config.GetConfigEntries() : new ConfigEntryBase[0];
        }

        //手动拖宽侧栏：0 = 自动；>0 = 用户拖出的宽度。
        private static float _sidebarW = 0f;
        private static bool _sidebarResizing;
        private static ConfigEntry<int> _sidebarWEntry; //设置页「栏目宽度」滑块（0=自动

        private static float SidebarWidth() {
            //两个来源都要生效：拖动侧栏右缘时用即时值 _sidebarW；否则用设置页「栏目宽度」配置项
            //（上一版只读配置项，导致拖动被忽略；只读 _sidebarW 又会被"设置改了但同步失效" 卡住。）
            float w = 0f;
            if (_sidebarResizing) w = _sidebarW;
            if (w <= 0f && _sidebarWEntry != null) w = _sidebarWEntry.Value;
            if (w <= 0f) w = _sidebarW;
            float auto = CalcAutoSidebarWidth();
            return w > 0f ? Mathf.Clamp(w, 100f, 320f) : auto;
        }

        private static float CalcAutoSidebarWidth() {
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
            //缓存：模式/分区/语言/缩放/外部插件不变时列宽不变，避免每帧对所有条目名 CalcSize
            string key = _mode + "|" + _selectedInternalSection + "|" + _langEn + "|" + _scaled + "|" + (CurrentExternalPlugin() != null ? CurrentExternalPlugin().guid : "");
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
            return key == "Collision Toggle Key" || key == "Grid Toggle Key";
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
            //描述：中文模式用 ZhDesc/配置描述；英文模式只用 ZhDesc 的英文表（查不到留空，不显示中文
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
                (entry.Definition.Key == "Collision Override" || entry.Definition.Key == "Grid Override");
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
            //进度锁定提示：文案走单一来源（ProgressionReasonText），不再就地手拼阈值文案
            string lockTip = ProgressionReasonText(entry);
            if (lockTip != null) tip += "\n🔒 " + lockTip;
            if (defText.Length > 0) {
                tip += "\n" + T("默认: " + defText, "Default: " + defText);
            }
            tip += "\n" + T("点击恢复默认值", "Click to reset to default");
            if (GUILayout.Button(new GUIContent(name, tip),
                _nameLabel, GUILayout.Width(nameW), GUILayout.Height(TextHeight(name, nameW)))) {
                try { entry.BoxedValue = entry.DefaultValue; } catch (Exception __ex) { Guard.Log("恢复条目默认值(点击名称)", __ex); }
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
                if (entry.Definition.Key == "Grid Override") pair = "Grid Toggle Key";
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
        //（按 文本+宽度+缩放 缓存，条目名固定时不再每帧 CalcHeight
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
        //  - 细轨道 + 已填充段（蓝）；圆形把手（normal/hover/active 三态反馈）
        //  - 左键拖拽把手或点击轨道直接跳转；悬停/拖拽时滚轮微调
        //  - 返回新值；**数值在松开左键时才提交**（_sliderCommitted）——拖动过程只预览不写配置，
        //    避免拖动中每帧写盘/触发联动（用户要求：松开左键时滑块数值生效）。
        private static bool _sliderCommitted;
        //拖动状态：用"控件几何"而非 hotControl/控件 id 认领拖动。原因：hotControl 与
        //GUIUtility.GetControlID 的 id 在 Layout / 输入事件 / Repaint 三种 pass 之间不保证一致，
        //一旦对不上，MouseDrag 就不会被认领 → 把手完全不动（用户反馈"所有滑块条都无法滑动）。
        //同时拖动中的 t 必须跨帧保存：调用方按"松开左键才提交"设计，不保存的话每帧都会从旧值反推 t 而弹回起点。
        private static bool _sliderDragActive;
        private static Rect _sliderDragRect;
        private static float _sliderDragT;

        //同一滑块条的几何匹配（浮点容差；拖动期间布局不变，容差只防亚像素抖动）
        private static bool SameSliderRect(Rect a, Rect b) {
            return Mathf.Abs(a.x - b.x) < 0.75f && Mathf.Abs(a.y - b.y) < 0.75f
                && Mathf.Abs(a.width - b.width) < 0.75f && Mathf.Abs(a.height - b.height) < 0.75f;
        }

        private static float SliderT(Rect rect, float trackW, float handleD, float mouseX) {
            return Mathf.Clamp01(Mathf.InverseLerp(rect.x + handleD / 2f, rect.x + handleD / 2f + trackW, mouseX));
        }

        private static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false) {
            _sliderCommitted = false;
            float trackH = Sc(4), handleD = Sc(16);
            float trackW = Mathf.Max(1f, rect.width - handleD);
            Event ev = Event.current;
            bool mine = _sliderDragActive && SameSliderRect(_sliderDragRect, rect);
            bool hover = rect.Contains(ev.mousePosition);
            float t = mine ? _sliderDragT : Mathf.InverseLerp(min, max, value);
            bool passive = ev.type == EventType.Repaint || ev.type == EventType.Layout;
            if (ev.type == EventType.MouseDown && ev.button == 0 && hover) {
                _sliderDragActive = true;
                _sliderDragRect = rect;
                t = SliderT(rect, trackW, handleD, ev.mousePosition.x);
                _sliderDragT = t;
                GUIUtility.hotControl = GUIUtility.GetControlID(14001, FocusType.Passive, rect);
                ev.Use();
            } else if (mine && (ev.type == EventType.MouseDrag || (passive && Input.GetMouseButton(0)))) {
                //拖动中：不管鼠标是否移出滑块范围都跟随，并钳制到 [0,1]。
                //MouseDrag 优先；万一某帧没收到 MouseDrag（事件被别的逻辑吞掉/顺序异常），
                //用"左键仍按住"兜底跟随，保证拖动一定跟手。
                t = SliderT(rect, trackW, handleD, ev.mousePosition.x);
                _sliderDragT = t;
                if (ev.type == EventType.MouseDrag) ev.Use();
            } else if (mine && ((ev.type == EventType.MouseUp && ev.button == 0) || (passive && !Input.GetMouseButton(0)))) {
                //松开左键：这一帧提交数值（漏收 MouseUp 时用"左键已松开"兜底）。
                _sliderDragActive = false;
                _sliderCommitted = true;
                GUIUtility.hotControl = 0;
                if (ev.type == EventType.MouseUp) ev.Use();
            } else if (ev.type == EventType.ScrollWheel && hover && !mine) {
                float step = intStep ? 1f : Mathf.Max((max - min) / 100f, 0.01f);
                value = Mathf.Clamp(value + (ev.delta.y > 0f ? -step : step) * (intStep ? 1f : 5f), min, max);
                ev.Use();
                return value;
            }
            //几何按最终 t 计算：轨道两端各留半个把手，使 t=0/1 时把手完整落在 rect 内，且与上面的鼠标映射一一对应
            Rect trackRect = new Rect(rect.x + handleD / 2f, rect.y + (rect.height - trackH) / 2f, trackW, trackH);
            Rect fillRect = new Rect(trackRect.x, trackRect.y, trackW * Mathf.Clamp01(t), trackH);
            Rect handleRect = new Rect(rect.x + trackW * Mathf.Clamp01(t), rect.y + (rect.height - handleD) / 2f, handleD, handleD);
            //绘制（Repaint 或任意帧都画，事件帧提前画把手以命中 hover）
            GUI.Box(trackRect, GUIContent.none, _sliderTrack);
            if (fillRect.width > 0.5f) GUI.Box(fillRect, GUIContent.none, _sliderFill);
            GUIStyle hs = mine ? _sliderHandleActive : (hover ? _sliderHandleHover : _sliderHandle);
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
                case "Sidebar Width": min = 0f; max = 320f; return true; //0 = 自动
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
                    text = RecText();
                } else {
                    KeyCode kc = (KeyCode)val;
                    //组合键显示：Shift/Ctrl/Alt + 主键（捕捉时按住修饰键即可设置组合）
                    text = ComboKeyDisplay(entry, kc);
                }
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(26)))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; _pendingModKey = KeyCode.None; _recSeq.Clear(); _recHeld.Clear(); _recLastAt = Time.unscaledTime; }
                }
            } else if (val is BepInEx.Configuration.KeyboardShortcut) {
                //combo keys (external mods like BetterFreeplay use KeyboardShortcut)
                bool capturing = _capturing == entry;
                string text = capturing ? RecText() : val.ToString();
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(26)))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; _pendingModKey = KeyCode.None; _recSeq.Clear(); _recHeld.Clear(); _recLastAt = Time.unscaledTime; }
                }
            } else if (val is Enum) {
                Type et = val.GetType();
                string cur = EnumDisplayName(val.ToString());
                //问号关卡 / EX 指定关卡下拉框：过滤掉不合适的关卡（空白/随机/原型 PROTOTYPE1-8），避免误选。
                bool qLevel = (entry.Definition.Section == "实验" && entry.Definition.Key == "Question Level")
                    || (entry.Definition.Section == "EX" && entry.Definition.Key == "Target Level");
                //缓存枚举选项（名称/值不变，语言切换时显示名每帧现算）：EX 页下拉框多，
                //避免每帧 Enum.GetNames/GetValues + 遍历构建列表造成卡顿
                object[] cachedVals;
                if (!_enumOptions.TryGetValue(et, out cachedVals)) {
                    string[] names = Enum.GetNames(et);
                    Array vals = Enum.GetValues(et);
                    List<object> vlist = new List<object>();
                    for (int i = 0; i < names.Length; i++) {
                        object v = vals.GetValue(i);
                        if (qLevel) {
                            if (names[i] == "BLANKLEVEL" || Convert.ToInt32(v) >= (int)GameState.LevelName.RANDOM) continue;
                        }
                        vlist.Add(v);
                    }
                    cachedVals = vlist.ToArray();
                    _enumOptions[et] = cachedVals;
                }
                string[] dispNames = new string[cachedVals.Length];
                for (int i = 0; i < cachedVals.Length; i++) dispNames[i] = EnumDisplayName(cachedVals[i].ToString());
                bool open;
                if (!_editOpen.TryGetValue(entry, out open)) open = false;
                //下拉框宽度与同页编辑框一致（EX 页窄：与金额/数量输入框同宽；普通页 170）
                float cbw = entry.Definition.Section == "EX" ? Sc(80) : Sc(170);
                int sel = ComboBox(entry, cur, cachedVals, dispNames, ref open, cbw);
                if (sel >= 0) {
                    SetValue(entry, cachedVals[sel]);
                    _editOpen[entry] = false;
                } else {
                    _editOpen[entry] = open;
                }
            } else if (val is int || val is float || val is string) {
                //视野 FOV slider (1-60); dragging never moves the window (slider grabs hot control)
                if (val is float && entry.Definition.Section == "视野" && entry.Definition.Key == "FOV") {
                    float fv = (float)val;
                    GUILayout.BeginHorizontal();
                    Rect sr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                    float nv = DrawSlider(sr, fv, 1f, 32f);
                    GUILayout.Label(nv.ToString("0"), _label, GUILayout.Width(Sc(44)), GUILayout.Height(Sc(26)));
                    GUILayout.EndHorizontal();
                    if (_sliderCommitted && Mathf.Abs(nv - fv) > 0.001f) SetValue(entry, nv);
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
                    if (_sliderCommitted && Mathf.Abs(nv - fv) > 0.001f) SetValue(entry, nv);
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
                    if (_sliderCommitted && Mathf.Abs(nv - fv) > 0.001f) SetValue(entry, isInt ? Mathf.RoundToInt(nv) : nv);
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
                //EX 页：编辑框宽度与同页下拉框一致（下拉框在 EX 段落用 Sc(80)，见上面 ComboBox 分支）。
                //原来只对 3 个硬编码键名生效，其余 EX 数值项仍是 170 → 与下拉框不齐。
                float editW = Sc(170);
                if (entry.Definition.Section == "EX") {
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

        //下拉框（全 mod 唯一实现，所有下拉框都走这里 → 行为完全统一）：
        //按钮与展开列表放在**同一个纵向组**里 → 列表天然落在按钮正下方、左边缘与按钮对齐；
        //按钮在整行最右边时，列表也跟着右对齐。不再需要任何"坐标缩进估算"
        //（历史三版错位的根因：旧实现把列表画在行外的下一行，只能用 rect 相减估缩进，
        //  估大了被推到可视区外、估小了跑到最左边，和下拉框完全脱节）。
        //列表项是普通 GUILayout.Button（点击天然命中，不穿透、无浮层）。
        //展开/收起当帧不画列表：IMGUI 的控件矩形来自上一趟 Layout，点击同一帧才新建出来的列表控件
        //没有 Layout 记录（矩形是垃圾值/零值）→ 会出现列表一闪就没、或被误判为点击。
        //延后一帧（下一帧 Layout 已包含列表）即可彻底避免。
        //注意 width：调用方传入的已是 Sc() 过的大小，这里不再二次缩放。
        private static int _comboSkipFrame = -1;

        private static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = Sc(170);
            int clicked = -1;
            //"本帧是否展开"用**进入时**的状态决定：保证本帧 Layout 与 Repaint 的控件结构完全一致
            //（中途点选只改下一帧的状态，绝不在同一帧里改变结构）
            bool drew = open && Time.frameCount != _comboSkipFrame;
            GUILayout.BeginVertical(GUILayout.Width(width));
            if (GUILayout.Button(current + "   ▾", _frame, GUILayout.Width(width), GUILayout.Height(Sc(26)))) {
                open = !open;
                _editOpen[entry] = open;
                _comboSkipFrame = Time.frameCount; //展开当帧不画列表，下一帧起
            }
            if (drew) {
                for (int i = 0; i < options.Length; i++) {
                    bool isSel = options[i] == current;
                    if (GUILayout.Button(options[i], isSel ? _selItem : _item, GUILayout.Width(width), GUILayout.Height(Sc(26)))) clicked = i;
                }
            }
            GUILayout.EndVertical();
            if (clicked >= 0) {
                open = false;
                _editOpen[entry] = false;
            }
            return clicked; //调用方按索引取 value 并落配置
        }

	}
}
