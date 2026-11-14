// 回合 HUD · 运行时绘制（DrawHud）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class HUD {
        private static void DrawHud() {
            if (_hudAlpha <= 0.004f) return;
            if (!SRApi.GateMaster) return;
            bool inGame = _hasGame && _phase != GameControl.GamePhase.NONE;
            if (!inGame) return;   // 大厅/树屋不画 HUD
            EnsureStyles();
            float hudA = _hudAlpha;
            float textA = Opacity() * hudA;
            float bgA = BgOpacity() * hudA;

            int n = 0;
            _segCount = 0;
            {
                Color cPhase = Col(_colPhase, Color.white);
                Color cCountdown = Col(_colCountdown, Color.white);
                Color cRound = Col(_colRound, Color.white);
                Color cLevel = Col(_colLevel, Color.white);
                Color cWin = Col(_colWin, Color.white);
                Color cExtra1 = Col(_colExtra1, Color.white);
                Color cExtra2 = Col(_colExtra2, Color.white);

                if (DisplayModeValue() == DisplayMode.Inline) {
                    // 平铺：每个显示项作为独立小段（各自背景/颜色），按可用宽度自动换行。
                    if (ShowOn(_showPhase)) AddSeg(cPhase, PhaseName(_phase));
                    if (ShowOn(_showCountdown)) AddSeg(CountdownColor(cCountdown), _timerText);
                    if (ShowOn(_showRound) && !string.IsNullOrEmpty(_roundText)) AddSeg(cRound, _roundText);
                    if (ShowOn(_showLevel) && !string.IsNullOrEmpty(_levelText)) AddSeg(cLevel, _levelText);
                    if (ShowOn(_showWin) && !string.IsNullOrEmpty(_winText)) AddSeg(cWin, _winText);
                    if (!string.IsNullOrEmpty(_extra1Text)) AddSeg(cExtra1, _extra1Text);
                    if (!string.IsNullOrEmpty(_extra2Text)) AddSeg(cExtra2, _extra2Text);
                } else {
                    if (ShowOn(_showPhase)) {
                        string s = PhaseName(_phase);
                        float f = SlotFadeChange(0, s);
                        AddLineFade(ref n, s, null, LinePhase, WithA(cPhase, textA * f));
                    }
                    if (ShowOn(_showCountdown)) {
                        float f = SlotFadeAppear(1, _timerText);
                        Color c = CountdownColor(cCountdown);
                        float blink = 1f;
                        if (_warnLevel == 2 && BlinkOn()) blink = (Mathf.Sin(Time.unscaledTime * 12f) > 0f) ? 1f : 0.25f;
                        AddLineFade(ref n, _timerText, null, LineTimer, WithA(c, textA * f * blink));
                    }
                    if (ShowOn(_showRound) && !string.IsNullOrEmpty(_roundText)) {
                        float f = SlotFadeChange(2, _roundText);
                        AddLineFade(ref n, _roundText, null, LineSmall, WithA(cRound, textA * f));
                    }
                    if (ShowOn(_showLevel) && !string.IsNullOrEmpty(_levelText)) {
                        float f = SlotFadeChange(3, _levelText);
                        AddLineFade(ref n, _levelText, null, LineSmall, WithA(cLevel, textA * f));
                    }
                    if (ShowOn(_showWin) && !string.IsNullOrEmpty(_winText)) {
                        float f = SlotFadeChange(4, _winText);
                        AddLineFade(ref n, _winText, null, LineSmall, WithA(cWin, textA * f));
                    }
                    if (!string.IsNullOrEmpty(_extra1Text)) {
                        float f = SlotFadeChange(5, _extra1Text);
                        AddLineFade(ref n, _extra1Text, null, LineSmall, WithA(cExtra1, textA * f));
                    }
                    if (!string.IsNullOrEmpty(_extra2Text)) {
                        float f = SlotFadeChange(6, _extra2Text);
                        AddLineFade(ref n, _extra2Text, null, LineSmall, WithA(cExtra2, textA * f));
                    }
                }
                AddRoundNoticeLine(ref n, textA);
            }
            if (n > _hudPlain.Length) n = _hudPlain.Length;
            if (n == 0 && _segCount == 0) return;   // 无任何内容（含平铺小段）时完全隐藏

            float pad = SRApi.Ctl.Sc(2f);
            float segPad = SRApi.Ctl.Sc(3f);
            float segGap = SRApi.Ctl.Sc(3f);
            float rowGap = SRApi.Ctl.Sc(2f);
            float segFont = (_smallStyle != null ? _smallStyle.fontSize : 14);
            float segH = segFont + segPad * 2f;
            float segBlockH = 0f;
            if (_segCount > 0) {
                float maxRowW = Mathf.Max(SRApi.Ctl.Sc(120f), (float)Screen.width - pad * 2f);
                float rowW = 0f;
                int row = 0;
                _segRowsW = 0f;
                for (int i = 0; i < _segCount; i++) {
                    float w = Measure(_segText[i], _smallStyle) + segPad * 2f;
                    // 小段宽度平滑（文本每秒变化时避免整行重排跳变）。
                    if (_heightFrame != Time.frameCount) {
                        if (_segWSmooth[i] <= 0f) _segWSmooth[i] = w;
                        else _segWSmooth[i] = Mathf.MoveTowards(_segWSmooth[i], w, SRApi.Ctl.Sc(480f) * Time.unscaledDeltaTime);
                    }
                    _segW[i] = _segWSmooth[i] > 0f ? _segWSmooth[i] : w;
                    if (rowW > 0f && rowW + segGap + w > maxRowW) { row++; rowW = 0f; }
                    if (rowW > 0f) rowW += segGap;
                    _segRow[i] = row;
                    _segX[i] = rowW;
                    rowW += w;
                    if (rowW > _segRowsW) _segRowsW = rowW;
                }
                _segRowCount = row + 1;
                segBlockH = _segRowCount * segH + (_segRowCount - 1) * rowGap;
            } else {
                _segRowCount = 0;
                _segRowsW = 0f;
            }
            float contentW = 0f;
            for (int i = 0; i < n; i++) contentW = Mathf.Max(contentW, Measure(_hudPlain[i], LineStyle(_hudKinds[i])));
            float panelW = Mathf.Max(Mathf.Max(contentW, _segRowsW) + pad * 2f, SRApi.Ctl.Sc(60f));
            float textW = Mathf.Max(1f, panelW - pad * 2f);
            float contentH = 0f;
            for (int i = 0; i < n; i++) {
                GUIStyle st = LineStyle(_hudKinds[i]);
                float th;
                if (_hudKinds[i] == LineRoundNotice && st != null) {
                    th = st.fontSize;   // 大号提示按当前动画字号占高
                } else {
                    th = 0f;
                    try { th = SRApi.Ctl.TextHeight(_hudPlain[i], textW); } catch { th = 0f; }
                    if (th <= 0f) th = (st != null ? st.fontSize : 14);
                }
                // 行高平滑：文本变化导致的高度差用 MoveTowards 过渡，避免整块 HUD 跳变。
                if (_hudKinds[i] == LineRoundNotice) {
                    _hudHeights[i] = th;   // 回合提示只有 1.5s，高度直接到位
                } else if (_heightFrame != Time.frameCount) {
                    if (_hudHeights[i] <= 0f) _hudHeights[i] = th;
                    else _hudHeights[i] = Mathf.MoveTowards(_hudHeights[i], th, SRApi.Ctl.Sc(160f) * Time.unscaledDeltaTime);
                }
                contentH += _hudHeights[i];
            }
            _heightFrame = Time.frameCount;
            float panelH = contentH + pad * 2f + segBlockH + ((segBlockH > 0f && n > 0) ? rowGap : 0f);
            _hudW = panelW;
            _hudH = panelH;

            // 3) 布局（从所在边滑入 6px，ease-out）+ 拖动命中。
            float x, y;
            LayoutPosition(_hudW, _hudH, out x, out y);
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, (float)Screen.width - panelW));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, (float)Screen.height - panelH));
            float slide = SRApi.Ctl.Sc(6f) * (1f - EaseOut(_hudAppear));
            float sx = 0f, sy = 0f;
            switch (LayoutValue()) {
            case HudLayout.TopLeft: sx = -slide; sy = -slide; break;
            case HudLayout.TopRight: sx = slide; sy = -slide; break;
            case HudLayout.BottomLeft: sx = -slide; sy = slide; break;
            case HudLayout.BottomRight: sx = slide; sy = slide; break;
            default: sx = -slide * 0.5f; break; // Custom：轻微从左侧滑入
            }
            Rect panel = new Rect(x + sx, y + sy, panelW, panelH);
            HandleDrag(panel);

            Event ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            GUI.depth = 1;

            GUI.color = new Color(0f, 0f, 0f, bgA);
            GUI.DrawTexture(panel, _white);
            GUI.color = Color.white;

            float ty = panel.y + pad;
            if (_segCount > 0 && _segRowCount > 0) {
                for (int r = 0; r < _segRowCount; r++) {
                    for (int i = 0; i < _segCount; i++) {
                        if (_segRow[i] != r) continue;
                        float easeSeg = StaggerAlpha(i);
                        if (easeSeg <= 0.001f) continue;
                        Rect sr = new Rect(panel.x + pad + _segX[i], ty + StaggerOffset(i) * SRApi.Ctl.Sc(3f), _segW[i], segH);
                        GUI.color = new Color(1f, 1f, 1f, 0.10f * easeSeg);   // 小段自身底色（比整块底稍亮）
                        GUI.DrawTexture(sr, _white);
                        GUI.color = Color.white;
                        _smallStyle.normal.textColor = WithA(_segColor[i], textA * easeSeg);
                        GUI.Label(new Rect(sr.x + segPad, sr.y, sr.width - segPad * 2f, sr.height), _segText[i], _smallStyle);
                    }
                    ty += segH + rowGap;
                }
                if (n > 0) ty += rowGap;
            }

            // 6) 普通行：逐行 stagger 淡入 + 轻微上移；富文本行用 GUI.color 的 alpha。
            float tw = panel.width - pad * 2f;
            for (int i = 0; i < n; i++) {
                GUIStyle st = LineStyle(_hudKinds[i]);
                if (st == null) { ty += _hudHeights[i]; continue; }
                float lh = _hudHeights[i];
                float easeLine = StaggerAlpha(i + _segCount);
                int kind = _hudKinds[i];
                // 回合提示只闪烁，不做位移；普通行位移用独立的 ease-out 进度。
                float yOff = kind == LineRoundNotice ? 0f : StaggerOffset(i + _segCount) * SRApi.Ctl.Sc(3f);
                Rect lr = new Rect(panel.x + pad, ty + yOff, tw, lh);
                Color c = _hudColors[i];
                st.normal.textColor = WithA(c, c.a * easeLine);
                GUI.Label(lr, _hudRich[i], st);
                ty += lh;
            }
        }
    }
}
