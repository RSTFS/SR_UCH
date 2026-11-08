// 控件门面 · 表格几何与操作列按钮（尺寸令牌 / 列宽计算 / 操作列命中）。
// 从 SR.Ctl.cs 拆出来只为控制单文件规模，行为不变。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {
    public static partial class Ctl {
        // 表格尺寸令牌：行高/表头高/拖拽条/滚动条宽/最小高度（全部随 UI 缩放，页面不再写死字面量）。
        // 依据列模板与当前语言表头文字计算默认列宽（只增不减的下限），供列表首次布局使用。
        public static float[] TableColumnWidths(SRApi.TableColumn[] cols) {
            if (cols == null) return null;
            float[] w = new float[cols.Length];
            for (int i = 0; i < cols.Length; i++) {
                SRApi.TableColumn c = cols[i];
                if (c == null) { w[i] = Sc(60f); continue; }
                string title = SRApi.T(c.TitleZh, c.TitleEn);
                float tw = 0f;
                try { tw = Label.CalcSize(new GUIContent(title)).x; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                w[i] = Mathf.Max(Sc(c.MinW), tw + Sc(c.PadW));
            }
            return w;
        }

        public static float TableRowH() { return Sc(26f); }

        public static float TableHeaderH() { return Sc(24f); }

        public static float TableGripH() { return Sc(4f); }

        public static float TableGripGap() { return Sc(2f); }

        public static float TableScrollbarW() { return Sc(13f); }

        public static float TableMinH() { return Sc(120f); }

        public static float TableAutoHeight(float usedAbove, float minH) {
            float avail = SRApi.PageViewportHeight > Sc(60f)
                ? SRApi.PageViewportHeight
                : (SR.ContentArea.height - Sc(26f) - Sc(2f) - SR.FooterHeight);
            return Mathf.Max(minH, avail - usedAbove - Sc(2f));
        }

        // 统一操作列按钮：在行矩形里依次画 BtnClip 按钮，返回本帧左键 MouseUp 命中并已执行回调的下标。
        // widths 为 null、长度不足或某项 <=0 时，该按钮宽度 = BtnClip.CalcSize(label).x + 11px（自适应文字）；
        // 多个按钮总宽超出行宽时按可用宽度统一收缩，绝不横向溢出。Enabled=false 的按钮画灰且不响应。
        // 操作列需要的宽度（核心化）：按按钮文字实测 + 每个按钮 11px 内边距 + 2px 间距 + 左右 2px。
        // 表格方把它当作"操作列"的默认宽即可自适应；返回设计单位（未乘 UI 缩放）。
        public static float TableActionColumnWidth(SRApi.TableAction[] actions) {
            try {
                if (actions == null || actions.Length == 0) return 0f;
                GUIStyle st = BtnClip;
                float scale = Sc(1f);
                if (scale <= 0f) scale = 1f;
                float total = Sc(2f) * 2f;
                int valid = 0;
                for (int i = 0; i < actions.Length; i++) {
                    SRApi.TableAction a = actions[i];
                    if (a == null) continue;
                    float w = (st != null ? st.CalcSize(SRApi.Ctl.Content(a.Label ?? "", null)).x : Sc(30f)) + Sc(11f);
                    if (valid > 0) total += Sc(2f);
                    total += w;
                    valid++;
                }
                if (valid == 0) return 0f;
                return Mathf.Ceil(total / scale);
            } catch (Exception __ex) { Guard.Log("操作列宽计算", __ex); return 0f; }
        }

        public static int TableActionButtons(Rect rowRect, SRApi.TableAction[] actions, float[] widths, ref int hoverIndex) {
            hoverIndex = -1;
            if (actions == null || actions.Length == 0) return -1;
            try {
                Event e = Event.current;
                Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
                float padX = Sc(2f);
                float y = rowRect.y + Sc(1f);
                float h = Mathf.Max(Sc(14f), rowRect.height - Sc(2f));
                float gap = Sc(2f);
                int n = actions.Length;
                float[] bw = new float[n];
                int valid = 0;
                float sum = 0f;
                for (int i = 0; i < n; i++) {
                    SRApi.TableAction a = actions[i];
                    if (a == null) continue;
                    float w;
                    if (widths != null && i < widths.Length && widths[i] > 0f) {
                        w = Sc(widths[i]);
                    } else {
                        GUIStyle st = BtnClip;
                        w = (st != null ? st.CalcSize(new GUIContent(a.Label ?? "")).x : Sc(30f)) + Sc(11f);
                    }
                    if (w <= 0f) w = Sc(8f);
                    bw[i] = w;
                    sum += w;
                    valid++;
                }
                if (valid == 0) return -1;
                float gapTotal = gap * Mathf.Max(0, valid - 1);
                // 可绘制宽度 = 行宽减去左侧起始内边距（右侧允许画到行边界，不再额外留 padX）。
                float avail = Mathf.Max(0f, rowRect.width - padX);
                float need = sum + gapTotal;
                if (need > avail) {
                    // 可用宽度不足：按钮和间距一起按比例收缩，保证最后一个按钮不越过行右边界。
                    float scale = need > 0f ? avail / need : 0f;
                    for (int i = 0; i < n; i++) if (bw[i] > 0f) bw[i] *= scale;
                    gap *= scale;
                }
                float x = rowRect.x + padX;
                int hit = -1;
                for (int i = 0; i < n; i++) {
                    SRApi.TableAction a = actions[i];
                    if (a == null) continue;
                    float curW = bw[i];
                    if (curW <= 0f) continue;
                    Rect br = new Rect(x, y, curW, h);
                    x += curW + gap;
                    if (br.xMax > rowRect.xMax) br.width = Mathf.Max(0f, rowRect.xMax - br.x);
                    if (br.width <= 0f) break;
                    bool over = br.Contains(mp);
                    bool enabled = a.Enabled;
                    if (over) hoverIndex = i;
                    if (over && enabled && !string.IsNullOrEmpty(a.Tip)) GUI.tooltip = a.Tip;
                    Color old = GUI.color;
                    try {
                        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
                        BtnClip.Draw(br, new GUIContent(a.Label ?? ""), over && enabled, false, false, false);
                    } finally { GUI.color = old; }
                    if (!enabled || e == null) continue;
                    if (e.type == EventType.MouseDown && e.button == 0 && over) BlockWindowDrag = true;
                    if (e.type == EventType.MouseUp && e.button == 0 && over) {
                        Action cb = a.OnClick;
                        if (cb != null) { try { cb(); } catch (Exception __ex2) { Guard.Log("操作列按钮回调", __ex2); } }
                        hit = i;
                        e.Use();
                    }
                }
                return hit;
            } catch (Exception __ex) {
                Guard.Log("操作列按钮", __ex);
                hoverIndex = -1;
                return -1;
            }
        }
    }
}
}
