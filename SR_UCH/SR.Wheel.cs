// 滚轮：单一「待处理量」模型。事件趟记量并消费事件，绘制趟由控件用自己的矩形取走，帧尾余量交页面滚动。
using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class SR {
        internal static class Wheel {
            private static bool _hasEvent;
            private static float _pending;        // 上滚为正
            private static Vector2 _mouse;        // 事件趟的鼠标（屏幕坐标）
            private static bool _shift, _ctrl;

            internal static float Value { get { return _pending; } }
            internal static bool HasPending { get { return _hasEvent && Mathf.Abs(_pending) > 0.5f; } }

            // 控件登记的悬停矩形与应用函数；按 key 去重，否则每趟登记会让列表无限增长。
            private sealed class Fb { public Rect Rect; public Func<float, bool> Apply; }
            private static readonly System.Collections.Generic.Dictionary<string, Fb> _fb =
                new System.Collections.Generic.Dictionary<string, Fb>(64);
            // 三种坐标候选各判一次：原始 / +区域偏移 / +区域偏移−页面滚动。
            // 统一命中判定：矩形可能来自内容坐标（滚动区内），鼠标是窗口坐标，
            // 故按「原始 / +区偏移 / +区偏移−页面滚动」三种候选各判一次。
            internal static bool HitAny(Rect rect, Vector2 mouse) {
                if (rect.Contains(mouse)) return true;
                Rect r1 = new Rect(rect.x + SR.AreaOffset.x, rect.y + SR.AreaOffset.y, rect.width, rect.height);
                if (r1.Contains(mouse)) return true;
                Vector2 ps = SRApi.PageScroll;
                Rect r2 = new Rect(rect.x + SR.AreaOffset.x - ps.x, rect.y + SR.AreaOffset.y - ps.y, rect.width, rect.height);
                return r2.Contains(mouse);
            }

            internal static void RegisterFallback(string key, Rect rect, Func<float, bool> apply) {
                try {
                    if (apply == null || rect.width < 2f || rect.height < 2f) return;
                    if (string.IsNullOrEmpty(key)) key = rect.x.ToString("0") + "," + rect.y.ToString("0");
                    Fb f;
                    if (!_fb.TryGetValue(key, out f)) { f = new Fb(); _fb[key] = f; }
                    f.Rect = rect; f.Apply = apply;
                } catch (Exception __ex) { SR.Guard.Log("滚轮/兜底登记", __ex); }
            }

            // 帧尾兜底命中：命中即消费并返回 true。
            internal static bool TryFallback() {
                if (!HasPending) return false;
                foreach (Fb __f2 in _fb.Values) {
                    Rect r = __f2.Rect;
                    bool hit = r.Contains(_mouse);
                    if (!hit) {
                        Rect r1 = new Rect(r.x + SR.AreaOffset.x, r.y + SR.AreaOffset.y, r.width, r.height);
                        hit = r1.Contains(_mouse);
                    }
                    if (!hit) {
                        Vector2 ps2 = SRApi.PageScroll;
                        Rect r2 = new Rect(r.x + SR.AreaOffset.x - ps2.x, r.y + SR.AreaOffset.y - ps2.y, r.width, r.height);
                        hit = r2.Contains(_mouse);
                    }
                    if (!hit) continue;
                    float v = _pending;
                    bool __used2 = false;
                    try { __used2 = __f2.Apply(v); } catch (Exception __ex) { SR.Guard.Log("滚轮/兜底应用", __ex); }
                    if (!__used2) continue;                // 到头/未消费：保留 pending，继续尝试
                    _pending = 0f; LastDecision = "fallback";
                    return true;
                }
                return false;
            }

            internal static string LastDecision = "none";   // 诊断：谁取走了这一格

            private static int _pendFrame = -1;

            // 事件趟：记下滚轮量并消费事件，之后由我们决定归属。
            // 同帧多次滚轮事件累加（不覆盖），否则连续两格只生效一格。
            internal static void FrameStart(Event e) {
                if (Time.frameCount != _pendFrame) { _pendFrame = Time.frameCount; _pending = 0f; _mouse = Vector2.zero; _shift = _ctrl = false; }
                _hasEvent = e != null && e.type == EventType.ScrollWheel;
                // 同一帧后续 OnGUI 趟无滚轮事件但仍有余量：鼠标与修饰键沿用事件趟。
                if (!_hasEvent) { _hasEvent = Mathf.Abs(_pending) > 0.01f; return; }
                try {
                    // 转发的是幅度（一次快速滚轮可能 ±2/±3），只取符号会让滚三格只走一格。
                    float v = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                    v = -v;                                  // Unity 向下滚为正 → 取反成"上滚为正"
                    if (Mathf.Abs(v) < 0.01f) return;
                    _mouse = e.mousePosition;
                    _shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    _ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    _pending += v;   // 同帧累加
                    LastDecision = "pending";   // 不在事件趟消费：控件在自己的绘制里直接取用
                } catch (Exception __ex) { SR.Guard.Log("滚轮/事件", __ex); }
            }

            internal static bool ShiftHeld { get { return _shift; } }
            internal static bool CtrlHeld { get { return _ctrl; } }

            // 控件绘制期调用：鼠标在 rect 内即取走该格并清零；rect 用绘制期自己的矩形。
            internal static float TakeIfInside(Rect rect) {
                // 同一趟同步处理：直接读本趟事件，命中即取走并标记已用。
                Event ev = Event.current;
                if (ev == null || ev.type != EventType.ScrollWheel) return 0f;
                if (Mathf.Abs(_pending) < 0.01f) {
                    float raw = Mathf.Abs(ev.delta.y) > 0.001f ? ev.delta.y : ev.delta.x;
                    _pending = -raw;   // 上滚为正
                }
                if (!HasPending && Mathf.Abs(_pending) < 0.01f) return 0f;
                // 两个鼠标来源都试：事件趟的 _mouse 与当前 Event.mousePosition。
                Vector2 nowMouse = _mouse;
                try { if (Event.current != null) nowMouse = Event.current.mousePosition; } catch (Exception __nm) { SR.Guard.Log("滚轮/实时鼠标", __nm); }
                if (!HitAny(rect, nowMouse) && !HitAny(rect, _mouse)) return 0f;
                // 控件矩形常在页面滚动内容坐标里：屏幕坐标 = 矩形 + 区域偏移 − 页面滚动。
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
                if (!HitAny(rect, nowMouse) && !HitAny(rect, _mouse)) return 0f;
                float v = _pending;
                _pending = 0f;
                LastDecision = "taken";
                try { Event ev2 = Event.current; if (ev2 != null) { ev2.Use(); } } catch (Exception __ue) { SR.Guard.Log("滚轮/标记已用", __ue); }
                SR.MarkWheelUsed();
                return v;
            }

            // 帧尾：页面滚动取用剩余滚轮量（没有则返回 0）。
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
