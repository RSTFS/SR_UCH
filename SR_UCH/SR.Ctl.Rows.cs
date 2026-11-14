// 控件门面 · 统一行控件：标签 + 控件（行下拉 / 行滑块 / 标签列宽）。
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {
    public static partial class Ctl {
        internal static void ResetLabelColumn() { _labelColPrev = 0f; _labelColCur = 0f; }

        internal static void EndLabelColumnFrame() { if (Mathf.Abs(_labelColCur - _labelColPrev) > 0.5f) _labelColPrev = _labelColCur; }

        // 标签列 token；间距与上下限只改这里。
        private static readonly float LabelMinW = Sc(48f);
        private static readonly float LabelMaxW = Sc(180f);
        private static readonly float LabelPadW = Sc(10f);
        private static readonly float LabelSlackW = Sc(60f);

        // 文本宽度缓存：按「语言版本 + UI 缩放」失效。
        private static readonly Dictionary<string, float> _labelWCache = new Dictionary<string, float>();
        private static int _labelWCacheLang = -1;
        private static float _labelWCacheScale = -1f;

        internal static float LabelColumnWidth(string label) {
            try {
                string __key = label ?? "";
                float __scale = Sc(1f);
                if (_labelWCacheLang != _langVer || Mathf.Abs(_labelWCacheScale - __scale) > 0.001f) {
                    _labelWCache.Clear(); _labelWCacheLang = _langVer; _labelWCacheScale = __scale;
                }
                float __cw;
                if (!_labelWCache.TryGetValue(__key, out __cw)) {
                    __cw = SR.Ctl.Label.CalcSize(SRApi.Ctl.Content(__key)).x;
                    _labelWCache[__key] = __cw;
                }
                float w = __cw + LabelPadW;
                if (w > _labelColCur) _labelColCur = w;
                float __prev = Mathf.Min(_labelColPrev, w + LabelSlackW);
                return Mathf.Max(LabelMinW, Mathf.Min(LabelMaxW, Mathf.Max(__prev, w)));
            } catch { return Sc(80f); }
        }

        // 点标签恢复默认值：仅左键 MouseDown 且指针在标签矩形内。
        private static void ResetOnLabelClick(Rect labelRect, ConfigEntryBase e) {
            try {
                Event ev = Event.current;
                if (e == null || ev == null || ev.type != EventType.MouseDown || ev.button != 0) return;
                if (!labelRect.Contains(ev.mousePosition)) return;
                object dv = e.SettingType == typeof(string) ? (object)"" : e.DefaultValue;
                if (!object.Equals(e.BoxedValue, dv)) {
                    SetValue(e, dv);
                    SRApi.Notify(T("已恢复默认值", "Reset to default"));
                }
                ev.Use();
            } catch (Exception __ex) { Guard.Log("统一行/恢复默认", __ex); }
        }

        public static void LabeledRow(string label, string tip, ConfigEntryBase e, float labelWidth) {
            try {
                GUILayout.BeginHorizontal();
                try {
                    float lw = labelWidth > 0f ? Sc(labelWidth)
                        : LabelColumnWidth(label);
                    GUILayout.Label(SRApi.Ctl.Content(label, tip), SR.Ctl.Label, GUILayout.Width(lw), GUILayout.Height(Sc(26)));
                    ResetOnLabelClick(GUILayoutUtility.GetLastRect(), e);
                    GUILayout.Space(Sc(6));
                    if (e != null) RenderControl(e);
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
            } catch (Exception __ex) { Guard.Log("统一行控件", __ex); }
            GUILayout.Space(Sc(2));
        }

        // 统一下拉行：entry 作为展开态 ID，选中即写值并收起。
        public static int RowCombo(string label, string tip, ConfigEntryBase e, string[] vals, string[] labels, float width, float labelWidth) {
            if (e == null || vals == null || labels == null || vals.Length == 0 || labels.Length == 0) return -1;
            int picked = -1;
            try {
                GUILayout.BeginHorizontal();
                try {
                    float lw = labelWidth > 0f ? Sc(labelWidth)
                        : LabelColumnWidth(label);
                    GUILayout.Label(SRApi.Ctl.Content(label, tip), SR.Ctl.Label, GUILayout.Width(lw), GUILayout.Height(Sc(26)));
                    ResetOnLabelClick(GUILayoutUtility.GetLastRect(), e);
                    GUILayout.Space(Sc(6));
                    string cur = e.BoxedValue as string ?? "";
                    int idx = 0;
                    for (int i = 0; i < vals.Length; i++) if (vals[i] == cur) { idx = i; break; }
                    idx = Mathf.Clamp(idx, 0, labels.Length - 1);
                    // 展开态须每帧从核心 EditOpen 取，否则局部变量恒 false，下拉永远打不开。
                    bool open = false;
                    if (SRApi.Ctl.EditOpen != null) SRApi.Ctl.EditOpen.TryGetValue(e, out open);
                    int sel = ComboBox(e, labels[idx], vals, labels, ref open, Sc(width));
                    if (SRApi.Ctl.EditOpen != null) SRApi.Ctl.EditOpen[e] = open;
                    if (sel >= 0 && sel < vals.Length) {
                        SetValue(e, vals[sel]);
                        picked = sel;
                        if (SRApi.Ctl.EditOpen != null) SRApi.Ctl.EditOpen[e] = false;   // 选中后明确收起
                    }
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
            } catch (Exception __ex) { Guard.Log("统一下拉行", __ex); }
            GUILayout.Space(Sc(2));
            return picked;
        }

        // 统一滑块行：0.1 步进 + 数值右对齐；返回本帧是否写入。
        public static bool RowSlider(string label, string tip, ConfigEntryBase e, float min, float max, float labelWidth, string fmt) {
            if (e == null || max <= min) return false;
            bool written = false;
            try {
                GUILayout.BeginHorizontal();
                try {
                    float lw = labelWidth > 0f ? Sc(labelWidth)
                        : LabelColumnWidth(label);
                    GUILayout.Label(SRApi.Ctl.Content(label, tip), SR.Ctl.Label, GUILayout.Width(lw), GUILayout.Height(Sc(26)));
                    ResetOnLabelClick(GUILayoutUtility.GetLastRect(), e);
                    GUILayout.Space(Sc(6));
                    float cur = (float)e.BoxedValue;
                    Rect r = GUILayoutUtility.GetRect(Sc(150f), Sc(28f));
                    // Ctrl+点击滑块转输入框，便于精确输入。
                    Event __ev = Event.current;
                    if (__ev != null && __ev.type == EventType.MouseDown && __ev.button == 0 && __ev.control && r.Contains(__ev.mousePosition)) {
                        NumEdit = e; NumText = cur.ToString("0.###"); __ev.Use();
                    }
                    if (ReferenceEquals(NumEdit, e)) {
                        string __t = SRApi.ClearableTextField(NumText ?? "", SRApi.Ctl.SearchBox, GUILayout.Width(Sc(90)), GUILayout.Height(Sc(26)));
                        if (__t != NumText) NumText = __t;
                        Event __ke = Event.current;
                        if (__ke != null && __ke.type == EventType.KeyDown) {
                            if (__ke.keyCode == KeyCode.Return || __ke.keyCode == KeyCode.KeypadEnter) {
                                float __nv2;
                                if (float.TryParse(NumText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out __nv2)) {
                                    SetValue(e, Mathf.Clamp(__nv2, min, max));
                                    written = true;
                                }
                                NumEdit = null; NumText = null; __ke.Use();
                            } else if (__ke.keyCode == KeyCode.Escape) { NumEdit = null; NumText = null; __ke.Use(); }
                        }
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        GUILayout.Space(Sc(2));
                        return written;
                    }
                    // 滑块按下时禁止拖窗口，否则会被当成空白拖动。
                    try {
                        Event __md = Event.current;
                        if (__md != null && __md.type == EventType.MouseDown && __md.button == 0 && r.Contains(__md.mousePosition))
                            BlockWindowDrag = true;
                    } catch (Exception __bw) { Guard.Log("滑块/禁止拖窗口", __bw); }
                    float v = DrawSlider(r, cur, min, max, false);
                    v = Mathf.Round(Mathf.Clamp(v, min, max) * 10f) / 10f;
                    if (Mathf.Abs(v - cur) > 0.0001f) { SetValue(e, v); written = true; }
                    GUILayout.Label(v.ToString(fmt), SR.Ctl.Label, GUILayout.Width(Sc(48)), GUILayout.Height(Sc(26)));
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
            } catch (Exception __ex) { Guard.Log("统一滑块行", __ex); }
            GUILayout.Space(Sc(2));
            return written;
        }
    }
}
}
