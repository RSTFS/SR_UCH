// 输入：打开/关闭键、EventSystem 门控、按帧滚轮仲裁，以及独立功能类 SRInputGate。
// SRInputGate 自行注册 Harmony 补丁；管理器本身不 patch 游戏。
using System;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Input（打开/关闭键 / EventSystem 门控 / 滚轮仲裁）；输入冻结补丁见文件末尾 SRInputGate。

        // 滚轮仲裁（按帧）：吃掉滚轮的界面件调用 MarkWheelUsed() 打标，
        // 读轴的每帧路径统一由 PostGuiWheel() 在 DrawGUI 之后处理，才能看到同一帧的标记。
        public static int WheelFrame = -1;
        public static void MarkWheelUsed() { WheelFrame = Time.frameCount; }
        public static bool WheelTakenThisFrame { get { return WheelFrame == Time.frameCount; } }

        // 最近一次滚轮事件量（向上滚为正），由每帧执行的 PostGuiWheel 记录，供 Update 阶段的功能取用。
        public static float WheelEventValue;
        public static float TakeWheelEvent() { //取用即清：一次滚轮只触发一次
            try {
                float d = WheelEventValue;
                if (Mathf.Abs(d) < 0.0001f) return 0f;
                WheelEventValue = 0f;
                //声明本次滚轮已被吃掉，避免 PostGuiWheel 在后续帧重复分发。
                MarkWheelUsed();
                return d;
            } catch { return 0f; }
        }

        //滚轮趟的统一收口：界面件之后决定这个滚轮给谁用（地图缩放 -> 自由相机缩放）
        public static void PostGuiWheel() {
            try {
                Event e = Event.current;
                if (e == null || e.type != EventType.ScrollWheel) return;   //一个滚轮事件只处理一趟
                // Unity 的 e.delta.y 向下滚为正，故取反以与既有约定一致。
                float wheel = 0f;
                try { wheel = Mathf.Abs(e.delta.y) > 0.001f ? -e.delta.y : -e.delta.x; } catch { wheel = 0f; }
                if (WheelTakenThisFrame) return;                            //本帧已被界面件吃掉
                if (Mathf.Abs(wheel) < 0.0001f) return;
                try { WheelEventValue = wheel; } catch { }
                //分发给已注册的滚轮消费者（地图缩放 / 自由相机缩放），消费逻辑由功能自己声明。
                if (FireWheel(wheel)) MarkWheelUsed();
            } catch (Exception __ex) { Guard.Log("滚轮仲裁", __ex); }
        }

        private static void CheckOpenKey() {
            if (_capturing != null) return;
            //只用 ComboKeyDown（修饰键按住 + 主键按下），组合键才能生效。
            if (SR.ComboKeyDown(_openKey)) {
                _visible = !_visible;
                if (!_visible) CloseMenu();
                else {
                    ApplyEventSystemGate();
                }
            }
        }
        // 面板打开时禁用游戏的 UGUI 事件系统以阻断点击穿透；EventSystem.current 在禁用期间为 null，故自留引用。
        // 门控须每帧跟随；"正在加入房间"等流程经 RegisterInputGateHold 登记挂起，挂起期间不禁用。
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

        //门控时记下 EventSystem 原本的启用状态，恢复时写回原值。
        private static bool _esGateWasEnabled = true;
        private static int _esGateScene = -1;

        private static void ApplyEventSystemGate() {
            //换场景后 EventSystem 可能已是新对象：按场景句柄判断，先把旧对象按原值恢复再重新认领。
            int sc = -1;
            try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { sc = -1; }
            if (sc != _esGateScene) {
                if (_gatedEventSystem != null) {
                    try { _gatedEventSystem.enabled = _esGateWasEnabled; } catch { }
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
                    //游戏自身可能已将其关闭，恢复时不可擅自打开
                    _esGateWasEnabled = _gatedEventSystem != null && _gatedEventSystem.enabled;
                }
                if (_gatedEventSystem != null) _gatedEventSystem.enabled = false;
            } else {
                if (_gatedEventSystem != null && _gatedEventSystem) _gatedEventSystem.enabled = _esGateWasEnabled;
                if (_gatedEventSystem != null) _esGateWasEnabled = true;
                _gatedEventSystem = null;
            }
        }

        internal static bool InputLocked {
            get { return (UiOpen && BlockInput) || AnyUiBlocked(); }
        }

	}

// 输入门控（独立功能类，不属于管理器框架）：打开面板 / 地图时冻结游戏输入的 6 个补丁。
// 置于本文件而非新建 .cs：csproj 为显式文件列表，新增文件不会被编译。
[SrFeature("sr.srinputgate", "SRInputGate", "1.0")]
public class SRInputGate : ITweak {

    public void Initialize(IFeatureHost plugin) {
        try { Harmony.CreateAndPatchAll(typeof(SRInputGate)); }
        catch (Exception e) { SR.LogError("输入门控 补丁注册失败: " + e.Message); }
    }

    //冻结规则：仅冻结本机 hasAuthority 的角色；树屋无条件冻结，其它场景跟随冻结角色开关。
    private static bool FreezeLocalCharacter(Character c) {
        if (!SR.InputLocked || !c.hasAuthority) return false;
        if (SR.Env.InTreehouse) return true; //树屋：无条件冻结自己
        return SR.PauseGame;                  //其它情况：跟随冻结角色开关
    }

    [HarmonyPatch(typeof(Character), "Update")]
    [HarmonyPrefix]
    static bool BlockGameInput(Character __instance) {
        //每帧补丁：异常须就地兜住，否则 Harmony 会每帧记一条日志且功能静默失效。
        try {
                return !FreezeLocalCharacter(__instance);
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽输入", __ex); return true; }
    }

    [HarmonyPatch(typeof(Character), "FixedUpdate")]
    [HarmonyPrefix]
    static bool BlockGameInputPhysics(Character __instance) {
        //每帧补丁：Harmony 每次异常都会记日志并静默失效，这里统一兜住并经 Guard.Log 留痕。
        try {
                if (FreezeLocalCharacter(__instance)) {
                    //清零速度，避免开面板期间进行中的跳跃 / 冲刺把角色带过地图。
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
        //每帧补丁：异常须就地兜住，否则 Harmony 会每帧记一条日志且功能静默失效。
        try {
                return !SR.InputLocked;
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽光标输入", __ex); return true; }
    }

    // 不跳过 GameControl.Update：它是回合相位与倒计时状态机，跳过会让回合开始 / 结算停住。
    [HarmonyPatch(typeof(GameControl), "Update")]
    [HarmonyPrefix]
    static bool BlockGameControl() {
        //仅地图编辑器打开时冻结游戏状态机；面板打开且对局外（树屋 / 大厅）同样冻结，对局内不冻结。
        try { return !(SR.InputLocked && !SR.Gate.InMatch); } catch { return !SR.InputLocked; }
    }

    //开面板（冻输入）/ 地图时屏蔽镜头跟随切换：滚轮或 LT+RT 会触发 checkCameraToggle 弹窗。
    [HarmonyPatch(typeof(Cursor), "checkCameraToggle")]
    [HarmonyPrefix]
    static bool BlockCameraToggle() {
        //每帧补丁：异常须就地兜住，否则 Harmony 会每帧记一条日志且功能静默失效。
        try {
                return !SR.InputLocked;
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽相机切换", __ex); return true; }
    }

    //开面板 / 地图时跳过 KeyboardInput，否则滚轮会被转成 RotateLeft/Right 事件切换视角。
    [HarmonyPatch(typeof(KeyboardInput), "Update")]
    [HarmonyPrefix]
    static bool BlockKeyboardInput() {
        //每帧补丁：异常须就地兜住，否则 Harmony 会每帧记一条日志且功能静默失效。
        try {
                return !SR.InputLocked;
        } catch (Exception __ex) { SR.Guard.Log("面板期屏蔽键盘输入", __ex); return true; }
    }
}

}
