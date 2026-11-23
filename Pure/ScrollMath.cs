// 纯算术；运行时与表驱动测试共用同一实现。
using System;

namespace SR_UCH.Pure {
    internal static class ScrollMath {
        internal static float Max(float a, float b) { return a > b ? a : b; }
        internal static float Min(float a, float b) { return a < b ? a : b; }
        internal static float Clamp(float y, float maxY) {
            if (maxY <= 0f || y <= 0f) return 0f;
            return y > maxY ? maxY : y;
        }
        internal static float Wheel(float y, float notches, float maxY, float step) {
            return Clamp(y - notches * step, maxY);
        }
        internal static bool Changed(float before, float after) {
            float d = after - before;
            return d > 0.01f || d < -0.01f;
        }
        internal static float TableRowsH(float tableH, float headerH) {
            return Max(0f, tableH - headerH);
        }
        internal static float TableMaxScrollY(int rows, float rowH, float rowsH) {
            float content = Max(0, rows) * Max(0f, rowH);
            return Max(0f, content - Max(0f, rowsH));
        }
        // 行区收敛到屏幕真实可见底界；rowH 保底，防止除零式空列表。
        internal static float RowsHVisible(float rowsTop, float raw, float visibleBottom, float rowH) {
            float avail = Max(rowH, visibleBottom - rowsTop);
            return Max(rowH, Min(Max(rowH, raw), avail));
        }
    }
}
