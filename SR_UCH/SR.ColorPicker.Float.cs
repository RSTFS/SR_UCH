// 调色板独立浮层窗口：点色块弹出，画在 SR 主窗口之上（可拖动标题栏、右上角关闭）。
using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

    // 分区：ColorPicker 浮层窗口
    internal static partial class ColorPicker {
        private static Rect _floatRect;      // 浮层矩形（屏幕坐标）
        private static bool _floatInit;      // 是否已定位过
        // 重新定位浮层（打开条目时调用，放本分部内以免跨文件访问私有字段）。
        internal static void ResetFloatPosition() { _floatInit = false; }
        // 内容宽度：页面内面板按浮层宽度排版（含内边距）。
        internal static float FloatContentWidth() {
            try { return Mathf.Max(Sc(200f), _floatRect.width - Sc(20f)); } catch (Exception) { return Sc(300f); }
        }
            // 浮层绘制：自绘窗口（标题栏拖动 / 关闭 / 内容区），画在其他覆盖层之上。
            internal static void DrawFloat() {
                if (!FloatOpen || OpenEntry == null) return;
                State s;
                if (!_states.TryGetValue(OpenEntry, out s) || !s.Open) { FloatOpen = false; return; }
                try {
                    float w = Mathf.Min(Sc(470f), Screen.width - Sc(24f));
                    float h = FloatHeight(w);
                    Event ev = Event.current;
                    if (!_floatInit) {
                        _floatInit = true;
                        Vector2 m = Input.mousePosition;
                        float mx = m.x, my = Screen.height - m.y;
                        _floatRect = new Rect(
                            Mathf.Clamp(mx + Sc(14f), Sc(6f), Mathf.Max(Sc(6f), Screen.width - w - Sc(6f))),
                            Mathf.Clamp(my + Sc(14f), Sc(6f), Mathf.Max(Sc(6f), Screen.height - h - Sc(6f))),
                            w, h);
                    }
                    _floatRect.width = w;
                    _floatRect.height = h;

                    // 标题栏拖动与关闭：用绝对矩形自绘，事件在 Group 之外处理，避免被内容区吃掉。
                    Rect title = new Rect(_floatRect.x, _floatRect.y, w, Sc(26f));
                    Rect close = new Rect(title.xMax - Sc(30f), title.y + Sc(3f), Sc(24f), Sc(20f));
                    if (ev != null) {
                        if (ev.type == EventType.MouseDown && ev.button == 0 && close.Contains(ev.mousePosition)) {
                            CloseFloat(OpenEntry, s); ev.Use(); return;
                        }
                        if (ev.type == EventType.MouseDown && ev.button == 0 && title.Contains(ev.mousePosition)) {
                            _floatDrag = true; _floatDragOff = ev.mousePosition - new Vector2(_floatRect.x, _floatRect.y); ev.Use();
                        } else if (ev.type == EventType.MouseDrag && _floatDrag) {
                            _floatRect.x = ev.mousePosition.x - _floatDragOff.x;
                            _floatRect.y = ev.mousePosition.y - _floatDragOff.y;
                            ev.Use();
                        } else if (ev.type == EventType.MouseUp && _floatDrag) { _floatDrag = false; ev.Use(); }
                    }
                    _floatRect.x = Mathf.Clamp(_floatRect.x, 0f, Mathf.Max(0f, Screen.width - w));
                    _floatRect.y = Mathf.Clamp(_floatRect.y, 0f, Mathf.Max(0f, Screen.height - h));

                    Color prevColor = GUI.color;
                    Font prevFont = GUI.skin.font;
                    int prevDepth = GUI.depth;
                    try {
                        // 浮层必须压在游戏 HUD 之上：深度取极小值（数值越小越靠上）。
                        GUI.depth = -1000;
                        if (_floatFont != null) GUI.skin.font = _floatFont;
                        // 面板底 + 边框
                        GUI.Box(_floatRect, GUIContent.none, Ctl.Footer);
                        GUI.Label(new Rect(title.x + Sc(10f), title.y + Sc(4f), w - Sc(48f), Sc(20f)),
                            T("调色板", "Palette"), Ctl.SecHeader);
                        if (GUI.Button(close, "×", Ctl.Btn)) { CloseFloat(OpenEntry, s); return; }
                        // 内容区：高度按内容实测（不留空白）
                        float contentH = h - Sc(32f);
                        GUI.BeginGroup(new Rect(_floatRect.x + Sc(10f), _floatRect.y + Sc(30f), w - Sc(20f), contentH));
                        try { DrawPanel(OpenEntry, s); } finally { GUI.EndGroup(); }
                        // 自绘光标：浮层在最上层，光标必须比浮层更靠上才看得见。
                        if (ev != null) {
                            Texture2D cur = SR.CursorTexture;
                            if (cur != null) {
                                GUI.depth = -2000;
                                Color __c = GUI.color;
                                try {
                                    GUI.color = Color.white;
                                    GUI.DrawTexture(new Rect(ev.mousePosition.x - Sc(7f), ev.mousePosition.y - Sc(7f), Sc(15f), Sc(15f)), cur);
                                } finally { GUI.color = __c; }
                            }
                        }
                    } finally {
                        GUI.color = prevColor;
                        GUI.skin.font = prevFont;
                        GUI.depth = prevDepth;
                    }
                } catch (Exception __ex) { Guard.Log("调色板浮层/绘制", __ex); }
            }

            // 浮层高度按内容估算（标题 + SV 方块高度 + 内边距），避免底部大片空白。
            private static float FloatHeight(float w) {
                try {
                    float side = Mathf.Clamp(w - Sc(220f), Sc(96f), Sc(200f));
                    return side + Sc(96f);
                } catch (Exception) { return Sc(300f); }
            }

            private static Font _floatFont = SR.UiFont;
            internal static void SetFloatFont(Font f) { _floatFont = f; }
            private static bool _floatDrag;
            private static Vector2 _floatDragOff;
    }
}
}
