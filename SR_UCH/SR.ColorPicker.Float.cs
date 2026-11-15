// 调色板独立浮层窗口：点色块弹出，画在 SR 主窗口之上（可拖动标题栏、右上角关闭）。
using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

    // 分区：ColorPicker 浮层窗口
    internal static partial class ColorPicker {
        // 重新定位浮层（下次打开时由核心 FloatWindow 按鼠标位置重新摆放）。
        internal static void ResetFloatPosition() { }
        // 内容宽度：页面内面板按浮层宽度排版（含内边距）。
        internal static float FloatContentWidth() {
            try { return Mathf.Max(Sc(200f), FloatWindow.Rect.width - Sc(20f)); } catch (Exception) { return Sc(300f); }
        }
            // 浮层绘制：统一走核心 FloatWindow（拖动 / 关闭 / 点外部关闭 / 字体 / 鼠标位置定位）。
        internal static void DrawFloat() {
            if (!FloatOpen) { SRApi.Ctl.FloatClose(); return; }
            if (!FloatWindow.IsOpen) {
                FloatWindow.Open(SRApi.T("调色板", "Palette"), new Vector2(440f, FloatHeight(440f) / Mathf.Max(0.0001f, Sc(1f))), DrawFloatContent, OnFloatClosed);
            }
        }

        private static void OnFloatClosed() { FloatOpen = false; ResetFloatPosition(); }

        // 主面板关闭时收起调色板（一类窗口：必须由 SR 界面触发/保持）。
        internal static void CloseFloatIfOpen() {
            try {
                if (!FloatOpen) return;
                FloatOpen = false;
                ResetFloatPosition();
                if (FloatWindow.IsOpen) FloatWindow.Close();
                if (OpenEntry != null) _states.Remove(OpenEntry);
                ClearOpenEntry();
            } catch (Exception __cf) { Guard.Log("调色板浮层/随面板收起", __cf); }
        }


        private static void DrawFloatContent() {
            State s;
            if (OpenEntry == null || !_states.TryGetValue(OpenEntry, out s) || !s.Open) { SRApi.Ctl.FloatClose(); return; }
            try { DrawPanel(OpenEntry, s); } catch (Exception __ex) { Guard.Log("调色板浮层/内容", __ex); }
        }

        private static float FloatHeight(float w) {
                try {
                    float side = Mathf.Clamp(w - Sc(220f), Sc(96f), Sc(200f));
                    return side + Sc(96f);
                } catch (Exception) { return Sc(300f); }
            }

            private static Font _floatFont;
            internal static void SetFloatFont(Font f) { _floatFont = f; }
            }
}
}
