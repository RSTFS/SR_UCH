// 滚轮仲裁（重写版）：整帧只有一个决策点。
// 依据 IMGUI 的唯一消费规则：ScrollWheel 一帧只能被 Use() 一次，滚动视图的位移在 EndScrollView 才应用，
// 所以"先画出来的控件"并不天然拥有事件 —— 必须由我们在绘制前统一裁决：控件 > 表格 > 页面滚动。
//
// 约定：
//   * 每个控件/表格在自己绘制时 Register(窗口坐标矩形, 处理器, 优先级)；这些区域下一帧生效
//     （滚轮事件发生在绘制之前，一帧的滞后对用户体验无影响，且避免"本帧布局还没算出来"的问题）。
//   * 处理器返回 true = 已消费；false = 不消费（例如列表已到顶/到底），继续交给下一层。
//   * 核心在每帧开头 FrameStart() 快照上一帧注册的区域，Consume() 按优先级裁决并负责 e.Use()。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class SR {
        internal static class Wheel {
            internal const int PrioControl = 0;   // 下拉框 / 滑块 / 滚动条
            internal const int PrioTable = 1;     // 表格行区
            internal const int PrioPageHook = 2;  // 页面级滚轮钩子

            private sealed class Zone {
                public Rect Rect;                 // 窗口坐标
                public Func<float, bool> Handler; // 参数：上滚为正的滚轮量
                public int Prio;
            }

            private static readonly List<Zone> _built = new List<Zone>();    // 本帧绘制时注册（下一帧生效）
            private static readonly List<Zone> _active = new List<Zone>();   // 上一帧注册的区域（本帧处理事件）
            private static readonly List<Func<Vector2, float, bool>> _pageHooks = new List<Func<Vector2, float, bool>>();

            private static bool _hasEvent;
            private static float _value;
            internal static Vector2 MouseWindow;   // 本帧鼠标（窗口坐标，与 Event.mousePosition 一致）
            internal static Vector2 MouseScreen;   // 本帧鼠标（屏幕坐标，页面钩子用）
            internal static float Value { get { return _value; } }   // 上滚为正

            internal static void FrameStart(Event e) {
                _active.Clear();
                _active.AddRange(_built);
                _built.Clear();
                _hasEvent = e != null && e.type == EventType.ScrollWheel;
                MouseWindow = e != null ? e.mousePosition : Vector2.zero;
                MouseScreen = MouseWindow;   // 屏幕坐标在 Consume 时用当帧 AreaOffset 现算（窗口可能刚移动）
                float v = 0f;
                if (_hasEvent) {
                    try { v = -(Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x); } catch { v = 0f; }
                }
                _value = v;
            }

            // 绘制期注册：窗口坐标矩形 + 处理器（优先级见 Prio*）
            internal static void Register(Rect windowRect, Func<float, bool> handler, int prio) {
                try {
                    if (handler == null || windowRect.width < 2f || windowRect.height < 2f) return;
                    if (_built.Count > 96) return;
                    _built.Add(new Zone { Rect = windowRect, Handler = handler, Prio = prio });
                } catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/注册", __ex); }
            }

            internal static void RegisterPageHook(Func<Vector2, float, bool> fn) {
                if (fn != null && !_pageHooks.Contains(fn)) _pageHooks.Add(fn);
            }

            // 返回 true = 本帧滚轮已处理（调用方应 e.Use() 并 MarkWheelUsed）
            internal static bool Consume() {
                if (!_hasEvent || Mathf.Abs(_value) < 0.0001f) return false;
                MouseScreen = MouseWindow + SR.AreaOffset;   // 用当帧偏移
                // 1) 控件 > 2) 表格：同一坐标系（窗口）按优先级取最靠前的一个
                Zone win = null;
                for (int i = 0; i < _active.Count; i++) {
                    Zone z = _active[i];
                    if (z == null || !z.Rect.Contains(MouseWindow)) continue;
                    if (win == null || z.Prio < win.Prio) win = z;
                }
                if (win != null) {
                    try { if (win.Handler(_value)) return true; }
                    catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/处理器", __ex); }
                }
                // 3) 页面级钩子（表格页保持原有实现，屏幕坐标）
                for (int i = 0; i < _pageHooks.Count; i++) {
                    try { if (_pageHooks[i](MouseScreen, _value)) return true; }
                    catch (Exception __ex) { SR.Guard.Log("滚轮仲裁/页面钩子", __ex); }
                }
                return false;
            }
        }
    }
}
