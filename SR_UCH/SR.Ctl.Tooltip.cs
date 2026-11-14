// 悬浮提示与换行标签（SR 侧实现；Ctl 门面里的 WrapLabel / DrawTooltip 只是转发）。
// 从 SR.Ctl.cs 拆出：纯提示绘制，与控件/行渲染无关。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

        //换行标签：按实际可用宽度测量高度，保证最后一行文字不被裁切
        private static void WrapLabel(string text) {
            float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
            GUILayout.Label(text, _labelWrap, GUILayout.Width(w), GUILayout.Height(TextHeight(text, w)));
        }

        // 紧凑版换行标签：高度用实测值，靠负间距把条目贴紧。
        private static void WrapLabelTight(string text) {
            float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
            GUILayout.Label(text, _labelWrap, GUILayout.Width(w), GUILayout.Height(TextHeight(text, w)));
            GUILayout.Space(-Sc(5)); // 贴紧：抵消样式自带的行距
        }

        // 悬浮提示框：最后绘制以保证在最上层；按最宽一行定宽，仅当整行超过屏幕 80% 才自动折行。
        // 测量结果按 tip 原文缓存（窗口尺寸变化时可能沿用旧 limit，tip 一变即重算）。
        private static string _tipKey;
        private static string[] _tipLines = new string[0];
        private static readonly GUIContent _tipContent = new GUIContent();
        private static float _tipW, _tipH, _tipInnerW;
        // 关闭界面时清空提示缓存，避免 tooltip 文本跨帧残留（表头提示延迟消失）。
        public static void ClearTip() {
            try { _tipKey = null; _tipLines = new string[0]; _tipContent.text = ""; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        // 悬浮文本专用样式：零内边距，高度由整块 CalcHeight 得出（不再逐行累加，避免虚高）。
        private static GUIStyle _tipText;
        private static GUIStyle TipTextStyle() {
            if (_tipText == null) {
                _tipText = new GUIStyle(_labelWrap);
                _tipText.padding = new RectOffset(0, 0, 0, 0);
                _tipText.margin = new RectOffset(0, 0, 0, 0);
                _tipText.wordWrap = true;
            }
            return _tipText;
        }
        private static bool[] _tipNoteLines = new bool[0];   // 逐行标记：true = 这一行是"注意"（染醒目色）
        private static string _tipShownKey;
        private static float _tipShownAt = -999f;

        private static void DrawTooltip(Vector2 mp) {
            string tip = GUI.tooltip;
            // 注意：Layout 帧里 GUI.tooltip 往往为空，不能因此重置计时，
            // 否则延迟永远走不完 → 所有悬浮说明看起来"消失"。空值时保持状态直接返回。
            if (string.IsNullOrEmpty(tip)) return;
            if (tip != _tipShownKey) { _tipShownKey = tip; _tipShownAt = Time.unscaledTime; }
            // 不做延迟：悬停即显示（淡入仍保留一帧内的轻微过渡）。
            float __alpha = 1f;
            // 测量与绘制统一用 _labelWrap（同一个字体/内边距），否则宽高会与绘制不一致导致裁字。
            if (tip != _tipKey) {
                _tipKey = tip;
                _tipContent.text = tip;
                _tipLines = tip.Split('\n');
                _tipNoteLines = new bool[_tipLines.Length];
                for (int i = 0; i < _tipLines.Length; i++) {
                    if (_tipLines[i].StartsWith("\u0002")) { _tipNoteLines[i] = true; _tipLines[i] = _tipLines[i].Substring(1); }
                }
                _tipContent.text = string.Join("\n", _tipLines);   // 测量用去掉标记后的文本
                GUIStyle st = TipTextStyle();
                float limit = Mathf.Min(Sc(600f), Screen.width * 0.62f);
                float maxLine = 0f;
                for (int i = 0; i < _tipLines.Length; i++) {
                    float lw = st.CalcSize(new GUIContent(_tipLines[i])).x;
                    if (lw > maxLine) maxLine = lw;
                }
                float innerW = Mathf.Max(Sc(40f), Mathf.Min(limit, maxLine + 2f));
                // 逐行测量：每行底部 +1px 缓冲（中文字形下沿常被 CalcHeight 少算，末笔会被裁）。
                // 整块高度 = 各行(高+1) 之和 + 上下内边距（各 3px），不再额外留 8px 的大空。
                float h = 0f;
                for (int i = 0; i < _tipLines.Length; i++) h += st.CalcHeight(new GUIContent(_tipLines[i]), innerW) + 1f;
                _tipInnerW = innerW;
                _tipW = innerW + Sc(10f);
                _tipH = h + Sc(6f);
            }
            float x = Mathf.Clamp(mp.x + 16f, 2f, Mathf.Max(2f, Screen.width - _tipW - 6f));
            float y = Mathf.Clamp(mp.y + 16f, 2f, Mathf.Max(2f, Screen.height - _tipH - 10f));
            Color __oldTipColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, __alpha);
            Rect __tipRect = new Rect(x, y, _tipW, _tipH);
            DrawRounded(__tipRect, SRApi.Ctl.ThemePanelBg, SRApi.Ctl.ThemeRadius);
            // 逐行绘制：desc 的第 3 行（注意）用醒目色；默认值/键行保持普通色
            {
                GUIStyle __ts = TipTextStyle();
                float __ty = __tipRect.y + Sc(3f);
                for (int li = 0; li < _tipLines.Length; li++) {
                    string __ln = _tipLines[li] ?? "";
                    bool __note = li < _tipNoteLines.Length && _tipNoteLines[li];
                    GUIContent __gc = new GUIContent(__ln);
                    float __lh = __ts.CalcHeight(__gc, _tipInnerW) + 1f;   // 每行底部 1px 缓冲
                    Color __pc = GUI.color;
                    if (__note) GUI.color = new Color(1f, 0.83f, 0.35f, __alpha);   // 注意行：琥珀色
                    GUI.Label(new Rect(__tipRect.x + Sc(5f), __ty, _tipInnerW, __lh), __gc, __ts);
                    GUI.color = __pc;
                    __ty += __lh;
                }
            }
            GUI.color = __oldTipColor;
        }

}
}
