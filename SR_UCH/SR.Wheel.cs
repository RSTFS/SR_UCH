// 滚轮仲裁（第三版：持久区域表，跨 OnGUI 趟稳定）。
//
// 为什么不是"上一帧注册、本帧用完清空"：Unity 一帧内 OnGUI 会被调用多次
// （Layout → Repaint → ScrollWheel …），每次进来都是新的 Event。若每趟都把注册表清空，
// 那么 ScrollWheel 那一趟拿到的区域取决于它前面跑过哪趟，滚动就会"有时灵有时不灵"。
// 因此这里用【按 id 持久化】的区域表：每次绘制用同一个 id 覆盖矩形，只有连续若干帧
// 不再注册的区域才被回收。滚轮事件趟读到的永远是最近一次绘制出的矩形。
//
// 优先级：控件（下拉框/滑块/滚动条） > 页面钩子（表格等） > 页面滚动。
// 处理器返回 true = 已消费；false = 让给下一层（例如列表已到顶）。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class SR {
        internal static class Wheel {
            internal const int PrioControl = 0;
            internal const int PrioPageHook = 2;

            private sealed class Zone {
                public Rect Local;                 // 注册时给的原始矩形（GUI 区域内坐标）
                public Rect Screen;                // 加上 AreaOffset 后的屏幕坐标
                public Func<float, bool> Handler;  // 上滚为正
                public int Prio;
                public int Stamp;                  // 最近一次注册的帧号
                // 两种坐标空间都判一次：Unity 的 Event.mousePosition 在 GUI 区域内到底是局部还是
                // 屏幕坐标，跨版本/跨上下文并不一致；只要任一空间命中就算命中，彻底绕开这个坑。
                public bool Hit(Vector2 mp) { return Local.Contains(mp) || Screen.Contains(mp); }
            }

            private static readonly Dictionary<string, Zone> _zones = new Dictionary<string, Zone>(64);
            private static readonly List<string> _stale = new List<string>(8);
            private static readonly List<Func<Vector2, float, bool>> _pageHooks = new List<Func<Vector2, float, bool>>();

            private static bool _hasEvent;
            private static float _value;
            internal static string LastDecision = "none";   // 诊断：本帧滚轮被哪一层消费（zone:<id> / hook / none）
            private static Vector2 _offset;   // 本帧 AreaOffset（区域与鼠标统一换算到屏幕坐标）

            internal static float Value { get { return _value; } }   // 上滚为正，一次滚轮 = ±1

            // 每趟 OnGUI 开头：只负责本趟事件信息，不动区域表。
            internal static void FrameStart(Event e) {
                _offset = SR.AreaOffset;
                _hasEvent = e != null && e.type == EventType.ScrollWheel;
                float v = 0f;
                if (_hasEvent) {
                    // 只取方向：不同输入设备/不同帧的 delta 大小不一致，乘上去就会出现"一次滚很远、一次滚一点"。
                    try { v = (Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x); } catch { v = 0f; }
                    v = v > 0f ? -1f : (v < 0f ? 1f : 0f);
                }
                _value = v;
            }

            // 绘制期注册/更新区域（id 必须稳定：如 "e:段名/键名"）。
            internal static void Register(string id, Rect windowRect, Func<float, bool> handler, int prio) {
                try {
                    if (string.IsNullOrEmpty(id) || handler == null) return;
                    if (windowRect.width < 2f || windowRect.height < 2f) return;
                    Zone z;
                    if (!_zones.TryGetValue(id, out z)) { z = new Zone(); _zones[id] = z; }
                    // 调用方给的是"布局坐标"（BeginArea 内），换算到屏幕坐标后再存
                    z.Local = windowRect;
                    z.Screen = new Rect(windowRect.x + _offset.x, windowRect.y + _offset.y, windowRect.width, windowRect.height);
                    z.Handler = handler; z.Prio = prio; z.Stamp = Time.frameCount;
                } catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/注册", __ex); }
            }

            internal static void RegisterPageHook(Func<Vector2, float, bool> fn) {
                if (fn != null && !_pageHooks.Contains(fn)) _pageHooks.Add(fn);
            }

            // 返回 true = 本帧滚轮已处理
            internal static bool Consume(Event e, Vector2 areaOffset, bool shift) {
                if (!_hasEvent || Mathf.Abs(_value) < 0.5f) return false;
                LastDecision = "none";
                // Event.mousePosition 本身就是屏幕坐标（只有 GUI 区域内的 rect 是局部坐标），
                // 以前这里又加了一次 AreaOffset，鼠标永远对不上区域 → 列表/滑块/下拉框全都收不到滚轮。
                Vector2 screen = e != null ? e.mousePosition : Vector2.zero;
                // 1) 控件区：按优先级依次尝试**所有命中**的区域（高优先级的不消费时，允许下一层继续）
                Vector2 mp = screen;
                int total = _zones.Count, hitCount = 0;
                for (int prio = PrioControl; prio <= PrioPageHook; prio++) {
                    if (shift && prio == PrioControl) continue;   // Shift = 横向滚动：滑块/下拉不参与
                    foreach (KeyValuePair<string, Zone> kv in _zones) {
                        Zone z = kv.Value;
                        if (z == null || z.Prio != prio || !z.Hit(mp)) continue;
                        hitCount++;
                        try {
                            if (z.Handler(_value)) { LastDecision = "zone:" + kv.Key; return true; }
                        } catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/处理器", __ex); }
                    }
                }
                LastDecision = "zone-miss(" + hitCount + "/" + total + ")";
                // 2) 页面钩子（表格等）：坐标空间同样两种都试一次
                for (int i = 0; i < _pageHooks.Count; i++) {
                    try {
                        if (_pageHooks[i](screen, _value)) { LastDecision = "hook"; return true; }
                        if (_pageHooks[i](screen + areaOffset, _value)) { LastDecision = "hook+off"; return true; }
                    } catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/页面钩子", __ex); }
                }
                return false;
            }

            // 清理长期未注册的区域（换页/窗口重建后旧矩形不会继续吃滚轮）。
            internal static void PruneZones() {
                try {
                    if (_zones.Count == 0) return;
                    int now = Time.frameCount;
                    _stale.Clear();
                    foreach (KeyValuePair<string, Zone> kv in _zones) {
                        if (kv.Value == null || now - kv.Value.Stamp > 5) _stale.Add(kv.Key);
                    }
                    for (int i = 0; i < _stale.Count; i++) _zones.Remove(_stale[i]);
                } catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/清理", __ex); }
            }
        }
    }
}
