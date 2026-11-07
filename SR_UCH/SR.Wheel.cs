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

            // 控件登记的"悬停矩形 + 应用函数"。按 key 去重：控件每趟（Layout/Repaint…）都会登记，
            // 若每次追加，列表会无限增长并触发上限清空 → 表现为"有时能滚有时不能"。
            private sealed class Fb { public Rect Rect; public Func<float, bool> Apply; }
            private static readonly System.Collections.Generic.Dictionary<string, Fb> _fb =
                new System.Collections.Generic.Dictionary<string, Fb>(64);
            // 矩形三种坐标候选（原始 / +区域偏移 / +区域偏移−页面滚动）各判一次
            private static bool HitAny(Rect rect, Vector2 mouse) {
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

            // 帧尾：尝试兜底命中（命中即消费并返回 true）
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

            // 在页面滚动视图建立**之前**命中：滚动视图会先吃掉滚轮，控件永远等不到事件。
            internal static bool TryHoverApply(float wheel) {
                if (Mathf.Abs(wheel) < 0.01f) return false;
                foreach (Fb __f in _fb.Values) {
                    Rect r = __f.Rect;
                    Vector2 mp = _mouse;
                    try { if (Event.current != null) mp = Event.current.mousePosition; } catch (Exception __m) { SR.Guard.Log("滚轮/命中鼠标", __m); }
                    if (!HitAny(r, mp)) continue;
                    bool __used = false;
                    try { __used = __f.Apply(wheel); } catch (Exception __ex) { SR.Guard.Log("滚轮/提前命中", __ex); }
                    if (!__used) continue;                 // 控件到头了：不算消费，留给后面
                    _pending = 0f;                         // 已消费：清待处理量，避免页面再滚一次
                    LastDecision = "hover";
                    try { SRApi.Diag("滚轮/控件命中 " + _fbKeyOf(__f) + " Δ" + wheel.ToString("0.0")); } catch (Exception __dg) { SR.Guard.Log("滚轮/命中诊断", __dg); }
                    return true;
                }
                return false;
            }

            private static string _fbKeyOf(Fb f) {
                try { return f != null ? (f.Rect.x.ToString("0") + "," + f.Rect.y.ToString("0")) : "?"; } catch { return "?"; }
            }

            internal static void ClearFallback() { _fb.Clear(); }
            internal static string LastDecision = "none";   // 诊断：谁取走了这一格

            private static int _pendFrame = -1;

            // 事件趟：记下滚轮量并**立刻消费事件**，之后只由我们决定归属。
            // 与 ImGui 一致：同一帧内多次滚轮事件是**累加**的（io.MouseWheel += wheel），
            // 不是后一次覆盖前一次；否则连续两格只会生效一格。
            internal static void FrameStart(Event e) {
                if (Time.frameCount != _pendFrame) { _pendFrame = Time.frameCount; _pending = 0f; _mouse = Vector2.zero; _shift = _ctrl = false; }
                _hasEvent = e != null && e.type == EventType.ScrollWheel;
                // 不再每趟清空：列表要跨趟保留（滚动视图在控件之前就吃掉滚轮，必须提前命中）
                // 同一帧的后续 OnGUI 趟没有滚轮事件，但仍有未取走的量：鼠标/修饰键沿用事件趟的
                if (!_hasEvent) { _hasEvent = Mathf.Abs(_pending) > 0.01f; return; }
                try {
                    // 与 ImGui 后端一致：转发的是**幅度**（一次快速滚轮可能给 ±2、±3），
                    // 不是只取符号；否则"滚三格只走一格"。
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

            // 控件绘制期调用：鼠标在 rect 内就把这一格取走（取走即清零）。rect 用绘制期自己的矩形。
            // 两种坐标空间都比一次，避免容器层级差异导致的错位。
            internal static float TakeIfInside(Rect rect) {
                // 与控件同一趟同步处理：直接读这一趟的事件，命中就取走并标记已用。
                Event ev = Event.current;
                if (ev == null || ev.type != EventType.ScrollWheel) return 0f;
                if (Mathf.Abs(_pending) < 0.01f) {
                    float raw = Mathf.Abs(ev.delta.y) > 0.001f ? ev.delta.y : ev.delta.x;
                    _pending = -raw;   // 上滚为正
                }
                if (!HasPending && Mathf.Abs(_pending) < 0.01f) return 0f;
                // 两个鼠标来源：事件趟记录的 _mouse，以及调用这一刻的 Event.mousePosition。
                // 控件判断"我是否悬停"时用的就是后者，所以两者都要试（不同趟/不同容器下可能不同）。
                Vector2 nowMouse = _mouse;
                try { if (Event.current != null) nowMouse = Event.current.mousePosition; } catch (Exception __nm) { SR.Guard.Log("滚轮/实时鼠标", __nm); }
                if (!HitAny(rect, nowMouse) && !HitAny(rect, _mouse)) return 0f;
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
                if (!HitAny(rect, nowMouse) && !HitAny(rect, _mouse)) return 0f;
                float v = _pending;
                _pending = 0f;
                LastDecision = "taken";
                try { Event ev2 = Event.current; if (ev2 != null) { ev2.Use(); } } catch (Exception __ue) { SR.Guard.Log("滚轮/标记已用", __ue); }
                SR.MarkWheelUsed();
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
