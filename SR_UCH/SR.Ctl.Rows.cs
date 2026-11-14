// 控件门面 · 统一行控件（标签 + 控件：行下拉 / 行滑块 / 标签列宽）。
// 从 SR.Ctl.cs 拆出来只为控制单文件规模，行为不变。
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

        // 标签列统一 token（所有页面/所有行控件共用；间距/上下限只改这里）。
        private static readonly float LabelMinW = Sc(48f);
        private static readonly float LabelMaxW = Sc(180f);
        private static readonly float LabelPadW = Sc(10f);
        private static readonly float LabelSlackW = Sc(60f);

        internal static float LabelColumnWidth(string label) {
            try {
                float w = SR.Ctl.Label.CalcSize(SRApi.Ctl.Content(label ?? "")).x + LabelPadW;
                if (w > _labelColCur) _labelColCur = w;
                // 对齐仍用上帧列宽，但不允许比当前标签宽出太多（旧实现会把别的页面的长标签宽度一直带过来）。
                float __prev = Mathf.Min(_labelColPrev, w + LabelSlackW);
                return Mathf.Max(LabelMinW, Mathf.Min(LabelMaxW, Mathf.Max(__prev, w)));
            } catch { return Sc(80f); }
        }

        // 点标签恢复默认值（复选框/滑块/编辑框/下拉通用）：只处理左键 MouseDown，且指针在标签矩形内。
        private static void ResetOnLabelClick(Rect labelRect, ConfigEntryBase e) {
            try {
                Event ev = Event.current;
                if (e == null || ev == null || ev.type != EventType.MouseDown || ev.button != 0) return;
                if (!labelRect.Contains(ev.mousePosition)) return;
                object dv = e.SettingType == typeof(string) ? (object)"" : e.DefaultValue;
                if (!object.Equals(e.BoxedValue, dv)) {
                    object __old = e.BoxedValue;
                    SetValue(e, dv);
                    // 带「撤销」动作：点击通知上的按钮恢复原值（NotifyEx 的核心化用法示例）。
                    SRApi.NotifyEx(T("已恢复默认值", "Reset to default"), 0, "reset", delegate {
                        try { SetValue(e, __old); SRApi.Notify(T("已撤销", "Undone")); }
                        catch (Exception __un) { Guard.Log("统一行/撤销恢复默认", __un); }
                    });
                }
                ev.Use();
            } catch (Exception __ex) { Guard.Log("统一行/恢复默认", __ex); }
        }

        // 统一行控件：标签按文字实测 + 右侧控件；与 RestoreLabel 的排版一致，页面不再各写一份。
        public static void LabeledRow(string label, string tip, ConfigEntryBase e, float labelWidth) {
            try {
                GUILayout.BeginHorizontal();
                try {
                    float lw = labelWidth > 0f ? Sc(labelWidth)
                        // 默认 = 本区最长标签宽度（与方块破坏页同一套对齐规则），再留 6px 间距
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

        // 统一下拉行：标签按文字实测，entry 作为稳定 ID 交给核心管理展开态，选中即写值并收起。
        public static int RowCombo(string label, string tip, ConfigEntryBase e, string[] vals, string[] labels, float width, float labelWidth) {
            if (e == null || vals == null || labels == null || vals.Length == 0 || labels.Length == 0) return -1;
            int picked = -1;
            try {
                GUILayout.BeginHorizontal();
                try {
                    float lw = labelWidth > 0f ? Sc(labelWidth)
                        // 默认 = 本区最长标签宽度（与方块破坏页同一套对齐规则），再留 6px 间距
                        : LabelColumnWidth(label);
                    GUILayout.Label(SRApi.Ctl.Content(label, tip), SR.Ctl.Label, GUILayout.Width(lw), GUILayout.Height(Sc(26)));
                    ResetOnLabelClick(GUILayoutUtility.GetLastRect(), e);
                    GUILayout.Space(Sc(6));
                    string cur = e.BoxedValue as string ?? "";
                    int idx = 0;
                    for (int i = 0; i < vals.Length; i++) if (vals[i] == cur) { idx = i; break; }
                    idx = Mathf.Clamp(idx, 0, labels.Length - 1);
                    // 展开态存在核心的 _editOpen（以配置条目为 key）：每帧必须以它为准，
                    // 否则局部变量恒 false，点击写进 EditOpen 后列表框永远不会被画出来（"下拉打不开"）。
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

        // 统一滑块行：0.1 步进 + 数值右对齐；返回本帧是否写入了新值。
        public static bool RowSlider(string label, string tip, ConfigEntryBase e, float min, float max, float labelWidth, string fmt) {
            if (e == null || max <= min) return false;
            bool written = false;
            try {
                GUILayout.BeginHorizontal();
                try {
                    float lw = labelWidth > 0f ? Sc(labelWidth)
                        // 默认 = 本区最长标签宽度（与方块破坏页同一套对齐规则），再留 6px 间距
                        : LabelColumnWidth(label);
                    GUILayout.Label(SRApi.Ctl.Content(label, tip), SR.Ctl.Label, GUILayout.Width(lw), GUILayout.Height(Sc(26)));
                    ResetOnLabelClick(GUILayoutUtility.GetLastRect(), e);
                    GUILayout.Space(Sc(6));
                    float cur = (float)e.BoxedValue;
                    Rect r = GUILayoutUtility.GetRect(Sc(150f), Sc(28f));
                    // Ctrl+点击滑块 → 变成输入框（ImGui 的 Ctrl+Click to input 做法）：
                    // 精确数值不用再靠一个"更多数值"开关，平时仍是滑块。
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
                    // 按下滑块时禁止拖窗口：我们放开了 hotControl 判定后，滑块按下会被当成"空白拖动"
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
