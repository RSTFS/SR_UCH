// 输入：打开/关闭键、EventSystem 门控、按帧滚轮仲裁，以及面板期冻结输入的本体补丁。
// 冻结补丁不实现 ITweak、不挂 [SrFeature]，由 SR.Initialize 显式注册。
using System;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Input（打开/关闭键 / EventSystem 门控 / 滚轮仲裁）

        // 滚轮仲裁（按帧）：吃掉滚轮的界面件调 MarkWheelUsed 打标，PostGuiWheel 在 DrawGUI 后统一处理。
        public static int WheelFrame = -1;
        public static void MarkWheelUsed() { WheelFrame = Time.frameCount; }
        public static bool WheelTakenThisFrame { get { return WheelFrame == Time.frameCount; } }

        // 最近一次滚轮事件量（上滚为正），由 PostGuiWheel 记录，供 Update 阶段功能取用。
        public static float WheelEventValue;
        public static float TakeWheelEvent() { // 取用即清
            try {
                float d = WheelEventValue;
                if (Mathf.Abs(d) < 0.0001f) return 0f;
                WheelEventValue = 0f;
                MarkWheelUsed();
                return d;
            } catch { return 0f; }
        }

        // 滚轮统一收口：界面件之后决定滚轮归谁（地图缩放 -> 自由相机缩放）。
        public static void PostGuiWheel() {
            try {
                Event e = Event.current;
                if (e == null || e.type != EventType.ScrollWheel) return;   // 每个事件只处理一趟
                // e.delta.y 向下滚为正，故取反；面板打开时界面优先，防穿透到世界缩放。
                float wheel = 0f;
                try { wheel = Mathf.Abs(e.delta.y) > 0.001f ? -e.delta.y : -e.delta.x; } catch { wheel = 0f; }
                if (WheelTakenThisFrame) return;                            // 本帧已被吃掉
                if (Mathf.Abs(wheel) < 0.0001f) return;
                if (SRApi.UiOpen) return;
                try { WheelEventValue = wheel; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (FireWheel(wheel)) MarkWheelUsed();
            } catch (Exception __ex) { Guard.Log("滚轮仲裁", __ex); }
        }

        private static void CheckOpenKey() {
            if (_capturing != null) return;
            // 只用 ComboKeyDown（修饰键 + 主键），组合键才能生效。
            if (SR.ComboKeyDown(_openKey)) {
                _visible = !_visible;
                if (!_visible) CloseMenu();
                else {
                    ApplyEventSystemGate();
                }
            }
        }
        // 面板打开时禁用 UGUI 事件系统以阻断点击穿透；EventSystem.current 禁用期间为 null，故自留引用。
        // 门控须每帧跟随；RegisterInputGateHold 登记挂起，挂起期间不禁用。
        private static readonly System.Collections.Generic.List<Func<bool>> _inputGateHolds = new System.Collections.Generic.List<Func<bool>>();
        public static void RegisterInputGateHold(Func<bool> hold) {
            if (hold != null && !_inputGateHolds.Contains(hold)) _inputGateHolds.Add(hold);
        }
        private static bool InputGateHeld() {
            for (int i = 0; i < _inputGateHolds.Count; i++) {
                try { if (_inputGateHolds[i]()) return true; } catch (Exception __ex) { Guard.Log("查询输入门控挂起", __ex); }
            }
            return false;
        }

        private static bool _esGateWasEnabled = true;
        private static int _esGateScene = -1;

        private static void ApplyEventSystemGate() {
            // 换场景后 EventSystem 可能是新对象：按场景句柄判断，先把旧对象按原值恢复再重新认领。
            int sc = -1;
            try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { sc = -1; }
            if (sc != _esGateScene) {
                if (_gatedEventSystem != null) {
                    try { _gatedEventSystem.enabled = _esGateWasEnabled; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
                _gatedEventSystem = null;
                _esGateWasEnabled = true;
                _esGateScene = sc;
            }
            if (InputLocked && !InputGateHeld()) {
                if (_gatedEventSystem == null || !_gatedEventSystem) {
                    _gatedEventSystem = UnityEngine.EventSystems.EventSystem.current;
                    if (_gatedEventSystem == null)
                        _gatedEventSystem = UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
                    _esGateWasEnabled = _gatedEventSystem != null && _gatedEventSystem.enabled;
                }
                if (_gatedEventSystem != null) _gatedEventSystem.enabled = false;
            } else {
                if (_gatedEventSystem != null && _gatedEventSystem) _gatedEventSystem.enabled = _esGateWasEnabled;
                if (_gatedEventSystem != null) _esGateWasEnabled = true;
                _gatedEventSystem = null;
            }
        }

        // 每帧只算一次：9 个每帧补丁会读数百次，避免每次走属性 + 布尔 + 委托分发。
        // 物理步可能读到上一帧的值，门控不需要亚帧精度。
        private static int _ilFrame = -1;
        private static bool _ilValue;
        internal static bool InputLocked {
            get {
                int f = Time.frameCount;
                if (f != _ilFrame) { _ilFrame = f; _ilValue = (UiOpen && BlockInput) || AnyUiBlocked(); }
                return _ilValue;
            }
        }

    }


// 本体补丁：面板 / 地图打开时冻结游戏输入，与 SR.Window 的 EmoteBlockPatch 同类。
// 不实现 ITweak、不挂 [SrFeature]、不进功能表 —— 这是框架行为，不是玩法功能。
internal static class SRInputGate {

    internal static void Register() {
        try { SRApi.RegisterPatch(typeof(SRInputGate)); }
        catch (Exception e) { SR.LogError("输入门控 补丁注册失败: " + e.Message); }
    }

    // 冻结规则：只冻结本机 hasAuthority 的角色；树屋无条件冻结，其它场景跟随 PauseGame。
    private static bool FreezeLocalCharacter(Character c) {
        if (!SR.InputLocked || !c.hasAuthority) return false;
        if (SR.Env.InTreehouse) return true; // 树屋无条件冻结
        return SR.PauseGame;                  // 跟随冻结角色开关
    }

    [HarmonyPatch(typeof(Character), "Update")]
    [HarmonyPrefix]
    static bool BlockGameInput(Character __instance) {
        // 每帧补丁：异常须就地兜住，否则 Harmony 每帧记日志且功能静默失效。
        try {
                return !FreezeLocalCharacter(__instance);
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽输入", __ex); return true; }
    }

    [HarmonyPatch(typeof(Character), "FixedUpdate")]
    [HarmonyPrefix]
    static bool BlockGameInputPhysics(Character __instance) {
        try {
                if (FreezeLocalCharacter(__instance)) {
                    Rigidbody2D rb = __instance.GetComponent<Rigidbody2D>();
                    if (rb != null) {
                        rb.velocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                    }
                    return false;
                }
                return true;
        } catch (Exception __ex) { SR.Guard.Log("面板期冻结角色", __ex); return true; }
    }

    [HarmonyPatch(typeof(PiecePlacementCursor), "Update")]
    [HarmonyPrefix]
    static bool BlockCursorInput() {
        try {
                return !SR.InputLocked;
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽光标输入", __ex); return true; }
    }

    // 不跳过 GameControl.Update：它是相位与倒计时状态机，跳过会让回合开始/结算停住。
    [HarmonyPatch(typeof(GameControl), "Update")]
    [HarmonyPrefix]
    static bool BlockGameControl() {
        // 仅面板打开且对局外（树屋/大厅）冻结状态机，对局内不冻结。
        try { return !(SR.InputLocked && !SR.Gate.InMatch); } catch { return !SR.InputLocked; }
    }

    // 开面板/地图时屏蔽镜头切换：滚轮或 LT+RT 会触发 checkCameraToggle 弹窗。
    [HarmonyPatch(typeof(Cursor), "checkCameraToggle")]
    [HarmonyPrefix]
    static bool BlockCameraToggle() {
        try {
                return !SR.InputLocked;
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽相机切换", __ex); return true; }
    }

    // 开面板/地图时跳过 KeyboardInput，否则滚轮会被转成旋转事件。
    [HarmonyPatch(typeof(KeyboardInput), "Update")]
    [HarmonyPrefix]
    static bool BlockKeyboardInput() {
        try {
                return !SR.InputLocked;
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽键盘输入", __ex); return true; }
    }
}

}
