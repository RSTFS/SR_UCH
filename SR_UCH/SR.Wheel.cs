// 滚轮：单一"待处理量"模型（成熟 IMGUI 的做法）。
//
// 为什么不是"区域表 + 命中判定"：那套依赖 Event.mousePosition 与 GUILayout 矩形各自处在哪个
// 坐标空间（窗口局部 / GUI 区域局部 / 滚动内容坐标），换一层容器就错位一次，症状是"滚轮偶尔生效、
// 偶尔去滚页面"。ImGui 的做法简单得多：
//   1) 事件趟：把 ScrollWheel 的滚轮量记成"待处理量"，并立刻 e.Use()，保证没有别的控件再滚。
//   2) 绘制趟：控件在自己画的时候用 **自己的矩形** 问一句"这个量归我吗？"（Wheel.TakeIfInside），
//      归它就取走并改值；因为矩形和鼠标都来自同一次绘制，不存在坐标空间错位。
//   3) 帧尾：还没人取走的量才交给页面滚动（或丢弃，如果本页声明不滚动）。
using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class SR {
        internal static class Wheel {
            internal const int PrioControl = 0;   // 兼容旧签名

            private static bool _hasEvent;
            private static float _pending;        // 上滚为正（一次滚轮 = ±1）
            private static Vector2 _mouse;        // 事件趟的鼠标（屏幕坐标）
            private static bool _shift, _ctrl;

            internal static float Value { get { return _pending; } }
            internal static bool HasPending { get { return _hasEvent && Mathf.Abs(_pending) > 0.5f; } }
            internal static string LastDecision = "none";   // 诊断：谁取走了这一格

            private static int _frame = -1;

            // 事件趟：记下滚轮量并**立刻消费事件**，之后只由我们决定归属。
            // 与 ImGui 一致：同一帧内多次滚轮事件是**累加**的（io.MouseWheel += wheel），
            // 不是后一次覆盖前一次；否则连续两格只会生效一格。
            internal static void FrameStart(Event e) {
                if (Time.frameCount != _frame) { _frame = Time.frameCount; _pending = 0f; _mouse = Vector2.zero; _shift = _ctrl = false; }
                _hasEvent = e != null && e.type == EventType.ScrollWheel;
                // 同一帧的后续 OnGUI 趟没有滚轮事件，但仍有未取走的量：鼠标/修饰键沿用事件趟的
                if (!_hasEvent) { _hasEvent = Mathf.Abs(_pending) > 0.01f; return; }
                try {
                    float v = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                    v = v > 0f ? -1f : (v < 0f ? 1f : 0f);   // 上滚为正
                    _mouse = e.mousePosition;
                    _shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    _ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    _pending += v;   // 同帧累加
                    LastDecision = "pending";
                    e.Use();               // 不让任何滚动视图/其它控件再处理
                    e.type = EventType.Used;
                    SR.MarkWheelUsed();
                } catch (Exception __ex) { SR.Guard.Log("滚轮/事件", __ex); }
            }

            internal static bool ShiftHeld { get { return _shift; } }
            internal static bool CtrlHeld { get { return _ctrl; } }

            // 控件绘制期调用：鼠标在 rect 内就把这一格取走（取走即清零）。rect 用绘制期自己的矩形。
            // 两种坐标空间都比一次，避免容器层级差异导致的错位。
            internal static float TakeIfInside(Rect rect) {
                if (!HasPending) return 0f;
                // 控件矩形通常处在"页面滚动内容"坐标里：屏幕坐标 = 矩形 + 区域偏移 − 页面滚动。
                // 三种候选各比一次（原始 / +区域偏移 / +区域偏移−页面滚动），命中任一即可：
                // 不同容器层级下 Unity 给的 mousePosition 空间并不统一，多比一次没有副作用。
                bool hit = rect.Contains(_mouse);
                if (!hit) {
                    Rect r1 = new Rect(rect.x + SR.AreaOffset.x, rect.y + SR.AreaOffset.y, rect.width, rect.height);
                    hit = r1.Contains(_mouse);
                }
                if (!hit) {
                    Vector2 ps = SRApi.PageScroll;
                    Rect r2 = new Rect(rect.x + SR.AreaOffset.x - ps.x, rect.y + SR.AreaOffset.y - ps.y, rect.width, rect.height);
                    hit = r2.Contains(_mouse);
                }
                if (!hit) return 0f;
                float v = _pending;
                _pending = 0f;
                LastDecision = "taken";
                return v;
            }

            // 帧尾：页面滚动取用剩余的滚轮量（没有则返回 0）。
            internal static float TakeRemaining(bool allowed) {
                if (!HasPending || !allowed) { if (HasPending) { _pending = 0f; LastDecision = "dropped"; } return 0f; }
                float v = _pending;
                _pending = 0f;
                LastDecision = "page";
                return v;
            }

            internal static Vector2 MouseScreen { get { return _mouse; } }
        }
    }
}
