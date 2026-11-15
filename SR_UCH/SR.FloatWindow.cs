// 独立浮窗基座，分两类：
//   一类 · 随主面板：调色板、右键重命名 —— 主面板关闭时一并收起（只在 DrawGUI 里画）。
//   二类 · 真独立：地图 —— 主面板关闭也照常显示，并可用快捷键（默认 M）随时开关。
// 共同特性：标题栏拖动、右上角关闭、点窗口外关闭、主题字体、自绘光标置顶、
//           首次打开左上角落在鼠标点击处（超出屏幕自动夹回）。
using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

    // 分区：FloatWindow（独立浮窗基座 + 统一派发）

    // 一类（随主面板）：只在主面板打开时存在。面板一关就收起，状态不残留。
    internal static void DrawAttachedWindows() {
        try {
            if (!_visible) {
                // 面板已关：把一类窗口全部收起（下次打开面板需要重新点开）。
                if (FloatWindow.IsOpen) FloatWindow.Close();
                ColorPicker.CloseFloatIfOpen();
                return;
            }
            Color prev = GUI.color;
            Matrix4x4 prevM = GUI.matrix;
            Font prevF = GUI.skin.font;
            int prevD = GUI.depth;
            try {
                GUI.matrix = Matrix4x4.identity;   // 浮窗用屏幕坐标，不吃主窗口缩放
                if (SRApi.Ctl.UiFont != null) GUI.skin.font = SRApi.Ctl.UiFont;
                FloatWindow.Draw();
                ColorPicker.DrawFloat();
            } finally {
                GUI.matrix = prevM; GUI.color = prev; GUI.skin.font = prevF; GUI.depth = prevD;
            }
        } catch (Exception __ex) { Guard.Log("绘制随面板浮窗", __ex); }
    }

    // 二类（真独立）：主面板关闭也画（地图）。
    internal static void DrawStandaloneWindows() {
        try {
            Color prev = GUI.color;
            Matrix4x4 prevM = GUI.matrix;
            Font prevF = GUI.skin.font;
            int prevD = GUI.depth;
            try {
                GUI.matrix = Matrix4x4.identity;
                if (SRApi.Ctl.UiFont != null) GUI.skin.font = SRApi.Ctl.UiFont;
                FreePlayMap.Draw();
            } finally {
                GUI.matrix = prevM; GUI.color = prev; GUI.skin.font = prevF; GUI.depth = prevD;
            }
        } catch (Exception __ex) { Guard.Log("绘制独立窗口", __ex); }
    }
    internal static class FloatWindow {
        internal static bool IsOpen { get; private set; }
        internal static Rect Rect;
        internal static string Title = "";
        internal static bool CloseOnOutsideClick = true;

        private static bool _placed, _dragging;
        private static Vector2 _dragOff;
        private static Action _onClose;
        private static Action _onDraw;
        private static Vector2 _size = new Vector2(360f, 160f);

        // 打开：size 为设计单位尺寸（内部再 Sc）；onDraw 只画内容区（不含标题栏）。
        internal static void Open(string title, Vector2 size, Action onDraw, Action onClose = null) {
            Title = title ?? "";
            _size = size;
            _onDraw = onDraw;
            _onClose = onClose;
            IsOpen = true;
            _placed = false;   // 下次绘制时定位到鼠标点击处
        }

        internal static void Close() {
            if (!IsOpen) return;
            IsOpen = false;
            _dragging = false;
            _onDraw = null;
            Action cb = _onClose;
            _onClose = null;
            try { if (cb != null) cb(); } catch (Exception __fc) { Guard.Log("浮窗/关闭回调", __fc); }
        }

        // 每帧由 DrawGUI 末尾调用（面板关闭时也调用：浮窗属于独立界面）。
        internal static void Draw() {
            if (!IsOpen || _onDraw == null) return;
            try {
                float w = Mathf.Min(SR.Ctl.Sc(_size.x), Screen.width - SR.Ctl.Sc(16f));
                float h = Mathf.Min(SR.Ctl.Sc(_size.y), Screen.height - SR.Ctl.Sc(16f));
                if (!_placed) {
                    _placed = true;
                    // 左上角 = 鼠标点击处（输入坐标是左下原点，GUI 用左上原点）。
                    Vector2 m = Input.mousePosition;
                    Rect = new Rect(m.x, Screen.height - m.y, w, h);
                }
                Rect.width = w; Rect.height = h;

                Event e = Event.current;
                Rect title = new Rect(Rect.x, Rect.y, w, SR.Ctl.Sc(26f));
                Rect close = new Rect(title.xMax - SR.Ctl.Sc(30f), title.y + SR.Ctl.Sc(3f), SR.Ctl.Sc(24f), SR.Ctl.Sc(20f));
                if (e != null) {
                    Vector2 mp = e.mousePosition;
                    if (e.type == EventType.MouseDown && e.button == 0) {
                        if (close.Contains(mp)) { Close(); e.Use(); return; }
                        if (title.Contains(mp)) { _dragging = true; _dragOff = mp - new Vector2(Rect.x, Rect.y); e.Use(); }
                        else if (!Rect.Contains(mp) && CloseOnOutsideClick) { Close(); e.Use(); return; }
                    } else if (e.type == EventType.MouseDrag && _dragging) {
                        Rect.x = mp.x - _dragOff.x; Rect.y = mp.y - _dragOff.y; e.Use();
                    } else if (e.type == EventType.MouseUp && _dragging) { _dragging = false; e.Use(); }
                }
                Rect.x = Mathf.Clamp(Rect.x, 0f, Mathf.Max(0f, Screen.width - w));
                Rect.y = Mathf.Clamp(Rect.y, 0f, Mathf.Max(0f, Screen.height - h));

                Color prevColor = GUI.color;
                Font prevFont = GUI.skin.font;
                int prevDepth = GUI.depth;
                try {
                    GUI.depth = -1000;                    // 压在主窗口与其他覆盖层之上
                    Font f = SRApi.Ctl.UiFont;
                    if (f != null) GUI.skin.font = f;
                    GUI.Box(Rect, GUIContent.none, SR.Ctl.Footer);
                    GUI.Label(new Rect(title.x + SR.Ctl.Sc(10f), title.y + SR.Ctl.Sc(4f), w - SR.Ctl.Sc(48f), SR.Ctl.Sc(20f)),
                        Title, SR.Ctl.SecHeader);
                    if (GUI.Button(close, "×", SR.Ctl.Btn)) { Close(); return; }
                    GUI.BeginGroup(new Rect(Rect.x + SR.Ctl.Sc(10f), Rect.y + SR.Ctl.Sc(30f), w - SR.Ctl.Sc(20f), h - SR.Ctl.Sc(38f)));
                    try { _onDraw(); } finally { GUI.EndGroup(); }
                    // 自绘光标：浮窗在最上层，光标必须更靠上才看得见。
                    if (e != null) {
                        Texture2D cur = SR.CursorTexture;
                        if (cur != null) {
                            GUI.depth = -2000;
                            Color c = GUI.color;
                            try {
                                GUI.color = Color.white;
                                GUI.DrawTexture(new Rect(e.mousePosition.x - SR.Ctl.Sc(7f), e.mousePosition.y - SR.Ctl.Sc(7f),
                                    SR.Ctl.Sc(15f), SR.Ctl.Sc(15f)), cur);
                            } finally { GUI.color = c; }
                        }
                    }
                } finally {
                    GUI.color = prevColor;
                    GUI.skin.font = prevFont;
                    GUI.depth = prevDepth;
                }
            } catch (Exception __ex) { Guard.Log("浮窗/绘制", __ex); }
        }
    }
}
}
