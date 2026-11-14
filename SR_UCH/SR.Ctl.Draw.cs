// 绘制原语：圆角面板 / 填充 / 线 / 就地滚轮轮询（自绘页面共用）。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

    // 绘制期就地滚轮：读 Input.mouseScrollDelta，用控件自己的矩形做命中，每帧每控件只应用一次。
    private static readonly Dictionary<string, int> _wheelPollFrame = new Dictionary<string, int>();
    public static float WheelPoll(string key, Rect rect) {
        try {
            Event e = Event.current;
            if (e == null || !rect.Contains(e.mousePosition)) return 0f;
            float d = 0f;
            try { d = Input.mouseScrollDelta.y; } catch (Exception __iw) { Guard.Log("滚轮/就地轮询", __iw); }
            // 某些环境里 Input.mouseScrollDelta 恒为 0，改用本趟 ScrollWheel 事件量。
            if (Mathf.Abs(d) < 0.01f && e.type == EventType.ScrollWheel) {
                float raw = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                d = -raw;
                if (Mathf.Abs(d) > 0.01f) { try { e.Use(); } catch (Exception __ue) { Guard.Log("滚轮/就地消费事件", __ue); } }
            }
            if (Mathf.Abs(d) < 0.01f) return 0f;
            int fr;
            if (_wheelPollFrame.TryGetValue(key ?? "", out fr) && fr == Time.frameCount) return 0f;
            if (_wheelPollFrame.Count > 256) _wheelPollFrame.Clear();
            _wheelPollFrame[key ?? ""] = Time.frameCount;
            MarkWheelUsed();
            return d;
        } catch (Exception __ex) { Guard.Log("滚轮/就地轮询外层", __ex); return 0f; }
    }

    // 表格通用绘制原语（三处列表共用）；纯白填充用常驻的 whiteTexture。
    public static Texture2D Px() { return Texture2D.whiteTexture; }
    // 圆角矩形：中心/边用纯色填充，四角用运行时生成并缓存的圆形 alpha 贴图。
    private static Texture2D _roundTex;
    private static Texture2D RoundTex() {
        if (_roundTex != null) return _roundTex;
        const int N = 32;
        _roundTex = new Texture2D(N, N, TextureFormat.ARGB32, false);
        _roundTex.hideFlags = HideFlags.HideAndDontSave;
        _roundTex.wrapMode = TextureWrapMode.Clamp;
        Color[] px = new Color[N * N];
        for (int y = 0; y < N; y++) {
            for (int x = 0; x < N; x++) {
                float dx = (x + 0.5f) / N * 2f - 1f;
                float dy = (y + 0.5f) / N * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((1f - d) * N * 0.5f);
                px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
        }
        _roundTex.SetPixels(px);
        _roundTex.Apply();
        return _roundTex;
    }

    public static void DrawRounded(Rect r, Color c, float radius) {
        try {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            float rad = Mathf.Clamp(radius, 0f, Mathf.Min(r.width, r.height) * 0.5f);
            if (rad <= 0.5f) { DrawFill(r, c); return; }
            Texture2D tex = RoundTex();
            Color old = GUI.color;
            GUI.color = c;
            DrawFill(new Rect(r.x + rad, r.y, r.width - rad * 2f, r.height), c);
            DrawFill(new Rect(r.x, r.y + rad, rad, r.height - rad * 2f), c);
            DrawFill(new Rect(r.xMax - rad, r.y + rad, rad, r.height - rad * 2f), c);
            DrawCorner(tex, new Rect(r.x, r.y, rad, rad), new Rect(0f, 0.5f, 0.5f, 0.5f));
            DrawCorner(tex, new Rect(r.xMax - rad, r.y, rad, rad), new Rect(0.5f, 0.5f, 0.5f, 0.5f));
            DrawCorner(tex, new Rect(r.x, r.yMax - rad, rad, rad), new Rect(0f, 0f, 0.5f, 0.5f));
            DrawCorner(tex, new Rect(r.xMax - rad, r.yMax - rad, rad, rad), new Rect(0.5f, 0f, 0.5f, 0.5f));
            GUI.color = old;
        } catch (Exception __ex) { Guard.Log("圆角绘制", __ex); }
    }

    private static void DrawCorner(Texture2D tex, Rect dst, Rect uv) {
        GUI.DrawTextureWithTexCoords(dst, tex, uv);
    }
    // 默认色 (1,1,1,0.22) 保持不变：本方法对外，功能页自绘的线也在用。
    public static void DrawLine(Rect r) { DrawLine(r, new Color(1f, 1f, 1f, 0.22f)); }

    public static void DrawLine(Rect r, Color c) { DrawFill(r, c); }

    // 纯色填充（仅 Repaint 趟画）；alpha <= 0 直接跳过，关闭底纹只需把 alpha 设为 0。
    public static void DrawFill(Rect r, Color c) {
        if (c.a <= 0f) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Color old = GUI.color;
        try {
        GUI.color = c;
        GUI.DrawTexture(r, Px());
        } finally { GUI.color = old; }
    }

    // 表格配色的只读视图：供功能页自绘「表外但属于表」的元素对齐。
    public static Color TableLineStrong { get { return CurStyle.LineStrong; } }
    public static void DrawRect(Rect r, float a) {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Color old = GUI.color;
        try {
        GUI.color = new Color(0.55f, 0.78f, 1f, a);
        GUI.DrawTexture(r, Px());
        } finally { GUI.color = old; }
    }

}
}
