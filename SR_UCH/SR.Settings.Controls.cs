// 设置页 · 条目控件基元：滑块 / 下拉框 / 条目行渲染 / 滚轮与悬停判定。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class SR {
        // 帧末清理：切场景 / 复位时清掉“展开当帧不绘制”的记录。
        internal static void SliderFrameEndCleanup() {
            _comboSkipFrame.Clear();
        }

        private static float DrawSlider(Rect rect, float value, float min, float max, bool intStep = false, float step = 0f) {
            float trackH = Sc(4), handleD = Sc(16);
            float trackW = Mathf.Max(1f, rect.width - handleD);

            // 输入层：不可见的原生 HorizontalSlider（空风格），只吃事件与热区，跨趟拖动稳定。
            if (SliderNative == null || SliderNativeThumb == null) {
                SliderNative = new GUIStyle();
                SliderNativeThumb = new GUIStyle();
                SliderNative.normal.background = null;
                SliderNativeThumb.normal.background = null;
            }
            // 滑块的按下/拖动须阻止窗口拖拽（HorizontalSlider 只认 hotControl，不会置 BlockWindowDrag）。
            try {
                Event __se = Event.current;
                if (__se != null && (__se.type == EventType.MouseDown || __se.type == EventType.MouseDrag)
                    && __se.button == 0 && rect.Contains(__se.mousePosition)) BlockWindowDrag = true;
            } catch (Exception __bd) { Guard.Log("滑块/禁止拖窗口", __bd); }
            float raw = GUI.HorizontalSlider(rect, value, min, max, SliderNative, SliderNativeThumb);
            try { if (GUIUtility.hotControl != 0 && rect.Contains(Event.current.mousePosition)) BlockWindowDrag = true; }
            catch (Exception __hc) { Guard.Log("滑块/热控件", __hc); }

            // 按调用方给的 step 量化；step<=0 时才按整数取整（大跨度下取整会拖不动）。
            float nv = raw;
            if (step > 0f) nv = Mathf.Round(nv / step) * step;
            else if (intStep) nv = Mathf.Round(nv);
            nv = Mathf.Clamp(nv, min, max);

            float t = Mathf.Clamp01(Mathf.InverseLerp(min, max, nv));
            Rect trackRect = new Rect(rect.x + handleD / 2f, rect.y + (rect.height - trackH) / 2f, trackW, trackH);
            Rect fillRect = new Rect(trackRect.x, trackRect.y, trackW * t, trackH);
            Rect handleRect = new Rect(rect.x + trackW * t, rect.y + (rect.height - handleD) / 2f, handleD, handleD);
            GUI.Box(trackRect, GUIContent.none, _sliderTrack);
            if (fillRect.width > 0.5f) GUI.Box(fillRect, GUIContent.none, _sliderFill);
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

        private static void RenderControl(ConfigEntryBase entry, bool isInternal) {
            CurIsInternal = isInternal;
            object val = entry.BoxedValue;
            if (val is bool) {
                bool b = (bool)val;
                // 当前模式不支持时置灰且不可勾选（例：自由相机只在自由模式可用）。
                bool allowed = Ctl.RowAllowed(entry);
                bool prevEnabled = GUI.enabled;
                if (!allowed) GUI.enabled = false;
                // 复选框只负责开关（左键切换），快捷键绑定走其左侧标签。
                if (GUILayout.Button(b && CheckTex == null ? "✓" : "", b ? _checkOn : _checkOff, GUILayout.Width(CtlH()), GUILayout.Height(Sc(26)))) {
                    SetValue(entry, !b);
                }
                if (!allowed) GUI.enabled = prevEnabled;
                // 勾贴图叠画在复选框上，贴图缺失时回退字形。
                if (b && CheckTex != null) {
                    Rect __ckr = GUILayoutUtility.GetLastRect();
                    float __ckm = __ckr.width * 0.18f;
                    GUI.DrawTexture(new Rect(__ckr.x + __ckm, __ckr.y + __ckm, __ckr.width - __ckm * 2f, __ckr.height - __ckm * 2f), CheckTex);
                }
            } else if (val is KeyCode) {
                bool capturing = _capturing == entry;
                string text;
                if (capturing) {
                    text = RecText();
                } else {
                    KeyCode kc = (KeyCode)val;
                    text = ComboKeyDisplay(entry, kc);
                }
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(CtlH()))) {
            // 左键也可开始录制；须同时记 CaptureStartedAt，否则超时判断立刻成立。
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; CaptureStartedAt = Time.unscaledTime; _recSeq.Clear(); _recHeld.Clear(); }
                }
                //键位框同样支持右键绑定 / 解绑
                ConfigEntry<KeyCode> kcRow = entry as ConfigEntry<KeyCode>;
                if (kcRow != null) TryBindByRightClick(kcRow, GUILayoutUtility.GetLastRect());
            } else if (val is BepInEx.Configuration.KeyboardShortcut) {
                //外部模组（如 BetterFreeplay）的组合键用 KeyboardShortcut
                bool capturing = _capturing == entry;
                string text = capturing ? RecText() : KbShortcutText((BepInEx.Configuration.KeyboardShortcut)val);
                if (GUILayout.Button(text, capturing ? _capture : _frame, GUILayout.Width(Sc(170)), GUILayout.Height(CtlH()))) {
                    if (!capturing) { _prevBoxed = val; _capturing = entry; _captureStartFrame = Time.frameCount; CaptureStartedAt = Time.unscaledTime; _recSeq.Clear(); _recHeld.Clear(); }
                }
            } else if (val is Enum) {
                Type et = val.GetType();
                string cur = EnumDisplayName(val.ToString());
            // 枚举选项按 entry 缓存（显示名每帧现算）；条目重建后旧数组失效，用 EntryVersion 整体失效。
                if (EnumOptionsVersion != EntryVersion) {
                    EnumOptionsVersion = EntryVersion;
                    _enumOptions.Clear();
                }
                object[] cachedVals;
                if (!_enumOptions.TryGetValue(entry, out cachedVals)) {
                    string[] names = Enum.GetNames(et);
                    Array vals = Enum.GetValues(et);
                    List<object> vlist = new List<object>();
                    for (int i = 0; i < names.Length; i++) {
                        object v = vals.GetValue(i);
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
                float hookW = SR.RowComboWidth(entry);
                float cbw = hookW > 0f ? Sc(hookW) : Sc(170);
                int sel = ComboBox(entry, cur, cachedVals, dispNames, ref open, cbw);
                if (sel >= 0) SetValue(entry, cachedVals[sel]);
                _editOpen[entry] = open;
            } else if (val is int || val is float || val is string) {
                float hmin, hmax;
                string hfmt;
                if ((val is float || val is int) && SR.RowSliderRange(entry, out hmin, out hmax, out hfmt)) {
                    float fv = val is int ? (int)val : (float)val;
                    float nv = fv;
                    float hstep = 0f;
                    if (val is int) { float span = hmax - hmin; hstep = span > 4000f ? span / 60f : 1f; }
                    Rect sr = default(Rect);
                    GUILayout.BeginHorizontal();
                    try {
                        sr = GUILayoutUtility.GetRect(Sc(150), CtlH(), GUILayout.Width(SliderTrackWidth()));
                        nv = DrawSlider(sr, fv, hmin, hmax, val is int, hstep); //int 条目按整数步进，提交时由 SetValue 归一化类型
                        {
                            // 按下滑块时禁止拖窗口（同上）
                            try {
                                Event __md2 = Event.current;
                                if (__md2 != null && __md2.type == EventType.MouseDown && __md2.button == 0 && sr.Contains(__md2.mousePosition))
                                    SRApi.BlockWindowDrag = true;
                            } catch (Exception __bw2) { Guard.Log("条目滑块/禁止拖窗口", __bw2); }
                            // 滑块不支持滚轮调值，仅拖动。
                        }
                        //float 条目可选步进（范围由 SRApi.RowSliders 给出，粒度由 RowSliderSteps 给出）
                        float fstep;
                        if (RowSliderStep(entry, out fstep))
                            nv = Mathf.Clamp(hmin + Mathf.Round((nv - hmin) / fstep) * fstep, hmin, hmax);
                        string __sv = nv.ToString(hfmt);
                        GUILayout.Label(__sv, _label, GUILayout.Width(SliderValueWidth(__sv)), GUILayout.Height(Sc(26)));
                        RenderFollowUp(entry, CurIsInternal);
                    } finally { GUILayout.EndHorizontal(); }
                    SliderSubmit(entry, nv);
                    return;
                }
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
                float hookEditW = SR.RowComboWidth(entry);
                float editW = hookEditW > 0f ? Sc(hookEditW) : Sc(170);
                string ntxt = ClearableTextField(txt, _searchBox, GUILayout.Width(editW), GUILayout.Height(Sc(26)));
                if (ntxt != txt) {
                    _editText[entry] = ntxt;
                    EditSelfFrame = Time.frameCount;   //标记改动由编辑框自身发起
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

        internal static void ComboMouseDownSeen() { ComboSeenFrame = Time.frameCount; }

        internal static void CloseOpenCombos() {
            try {
                //先拷贝键再改值：边枚举边写会抛 "Collection was modified" 异常。
                ConfigEntryBase[] keys = new ConfigEntryBase[_editOpen.Count];
                _editOpen.Keys.CopyTo(keys, 0);
                for (int i = 0; i < keys.Length; i++) if (_editOpen[keys[i]]) _editOpen[keys[i]] = false;
            } catch (Exception __ex) { Guard.Log("收起展开的下拉框", __ex); }
        }

        private static float SliderValueWidth(string text) {
            try {
                return Mathf.Max(Sc(44), _label.CalcSize(new GUIContent(text)).x + Sc(8));
            } catch { return Sc(44); }
        }

        internal static void ResetWheelState() {
            try {
                WheelShieldHit = false;
                _sliderLastWritten.Clear();   //跨场景后配置可能已变，清空已写入值表
            } catch (Exception __ex) { Guard.Log("ResetWheelState", __ex); }
        }

        private static float CtlH() { try { return Sc(ControlHeight > 0f ? ControlHeight : 26f); } catch { return Sc(26); } }

        public static float SliderTrackWidth() { try { return Sc(150); } catch { return 150f; } }

        // 一帧只允许一个下拉消费滚轮：Layout 与 Repaint 都会轮询，否则一格滚两格。
        private static int _comboWheelFrame = -1;
        internal static bool ComboWheelConsumedThisFrame { get { return _comboWheelFrame == Time.frameCount; } }

        private static int NextComboSeq() {
            if (ComboSeqFrame != Time.frameCount) { ComboSeqFrame = Time.frameCount; ComboSeq = 0; }
            return ComboSeq++;
        }

        private static int ComboBox(ConfigEntryBase entry, string current, Array vals, string[] options, ref bool open, float width = -1f) {
            if (width <= 0f) width = Sc(170);
            int clicked = -1;
            // “本帧是否展开”按帧锁存：逐趟按实时 open 决定会让 Repaint 与 Layout 控件数不一致并抛异常。
            // lambda 不能捕获 ref 参数 open，先拷到局部变量。
            bool __openNow = open;
            if (open) SR.Ctl.NoteComboOpen();   // 本帧有下拉展开：页面滚动区让出滚轮
            bool __skipNow = entry != null && _comboSkipFrame.TryGetValue(entry, out int __sf) && __sf == Time.frameCount;
            GUILayout.BeginVertical(GUILayout.Width(width));
            if (GUILayout.Button(current + "   v", _frame, GUILayout.Width(width), GUILayout.Height(CtlH()))) {
                ComboMouseDownSeen();   //本下拉框被点击
                open = !open;
                if (entry != null) _editOpen[entry] = open;   //null 保护：字典不接受 null 键
                if (entry != null) _comboSkipFrame[entry] = Time.frameCount; //展开当帧不绘制列表
            }
            Rect boxRect = GUILayoutUtility.GetLastRect();
            SR.Ctl.NoteComboRect(boxRect);   // 收起态：滚轮在这里换选项，窗口据此拦截页面滚动
            // 锁存键须每帧每下拉框唯一且逐趟稳定：entry 可能为 null，故改用本帧调用序号。
            string __latchKey = "combo#" + NextComboSeq() + ":" + (entry != null ? entry.Definition.Section + "/" + entry.Definition.Key : "?");
            bool drew = SR.Ctl.FrameLatch(__latchKey, delegate () { return __openNow && !__skipNow; });
            // 滚轮：绘制期就地轮询（唯一路径）；收起换选项、展开滚列表，均一格一项。
            if (options != null && options.Length > 1) {
                float __w = 0f;
                if (drew) {
                    int __vis2 = Mathf.Min(options.Length, 8);
                    __w = WheelPoll(__latchKey + ":list", new Rect(boxRect.x, boxRect.yMax, width, CtlH() * __vis2));
                    if (Mathf.Abs(__w) > 0.01f) {
                        int __sc;
                        if (!_comboScroll.TryGetValue(entry, out __sc)) __sc = 0;
                        _comboScroll[entry] = Mathf.Clamp(__sc + (__w > 0f ? -1 : 1), 0, Mathf.Max(0, options.Length - 8));
                        __w = 0f;
                    }
                } else {
                    __w = WheelPoll(__latchKey, boxRect);
                }
                bool __canApply = Mathf.Abs(__w) > 0.01f && _comboWheelFrame != Time.frameCount;
                if (__canApply) _comboWheelFrame = Time.frameCount;
                if (__canApply) {
                    // 同一帧只让一个下拉吃滚轮，否则 Layout/Repaint 两趟各应用一次。
                    int __cur = 0;
                    for (int i = 0; i < options.Length; i++) { if (options[i] == current) { __cur = i; break; } }
                    // 一格 = 一项，不按幅度钳制。
                    int __n2 = Mathf.Abs(__w) < 0.5f ? 1 : Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(__w)));
                    int __ni = __w > 0f ? __cur - __n2 : __cur + __n2;
                    while (__ni < 0) __ni += options.Length;
                    while (__ni >= options.Length) __ni -= options.Length;
                    if (entry != null && vals != null && __ni < vals.Length) SR.SetValue(entry, vals.GetValue(__ni));
                    // 关键：结果要当作选中项返回；entry == null 的页面自持状态只能靠返回值更新。
                    clicked = __ni;
                }
            }
            // 键盘导航：展开时 Up/Down 换项、Enter/Esc 收起。
            if (open && entry != null && options != null && options.Length > 1) {
                Event __ke2 = Event.current;
                if (__ke2 != null && __ke2.type == EventType.KeyDown) {
                    int __cur2 = 0;
                    for (int i = 0; i < options.Length; i++) { if (options[i] == current) { __cur2 = i; break; } }
                    bool __next = __ke2.keyCode == KeyCode.DownArrow || (__ke2.keyCode == KeyCode.Tab && !__ke2.shift);
                    bool __prev2 = __ke2.keyCode == KeyCode.UpArrow || (__ke2.keyCode == KeyCode.Tab && __ke2.shift);
                    if (__next || __prev2) {
                        int __n3 = __next ? (__cur2 + 1) % options.Length : (__cur2 - 1 + options.Length) % options.Length;
                        if (vals != null && __n3 < vals.Length) SR.SetValue(entry, vals.GetValue(__n3));
                        __ke2.Use();
                        __ke2.Use();
                    } else if (__ke2.keyCode == KeyCode.UpArrow) {
                        int __n3 = (__cur2 - 1 + options.Length) % options.Length;
                        if (vals != null && __n3 < vals.Length) SR.SetValue(entry, vals.GetValue(__n3));
                        __ke2.Use();
                    } else if (__ke2.keyCode == KeyCode.Return || __ke2.keyCode == KeyCode.KeypadEnter || __ke2.keyCode == KeyCode.Escape) {
                        open = false;
                        if (entry != null) _editOpen[entry] = false;
                        __ke2.Use();
                    }
                }
            }
            if (drew) {
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
                SR.Ctl.NoteComboRect(new Rect(boxRect.x, boxRect.yMax, width, CtlH() * (__last - __first)));
                for (int i = __first; i < __last; i++) {
                    bool isSel = options[i] == current;
                    if (GUILayout.Button(options[i], isSel ? _selItem : _item, GUILayout.Width(width), GUILayout.Height(CtlH()))) clicked = i;
                }
            }
            GUILayout.EndVertical();
            if (clicked >= 0) {
                ComboMouseDownSeen();   //列表项被点击
                open = false;
                if (entry != null) _editOpen[entry] = false;
            }
            return clicked; //调用方按索引取 value 并落配置
        }
    }
}
