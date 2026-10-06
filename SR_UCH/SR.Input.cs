// 输入。打开/关闭键、EventSystem 门控、按帧的滚轮仲裁。
// 里面的 SRInputGate 是个独立功能类，Harmony 补丁它自己装（管理器本身不 patch 游戏）。
using System;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Input（打开/关闭键 / EventSystem 门控 / 滚轮仲裁）
// （输入冻结 / 角色冻结的 6 个补丁已搬到底部独立类 SRInputGate：那是功能，不是管理器框架）

        // 滚轮仲裁（按帧）
        //为什么需要：IMGUI 里"谁先处理谁 Use() 吃掉事件"只对 IMGUI 事件有效；而地图缩放 / 自由相机缩放 /
        //聊天框字号缩放是用 Input.GetAxis("Mouse ScrollWheel") 读的, 它们看不到事件有没有被界面吃掉，
        //于是会出现"在下拉框/滑块/联机列表上滚轮，地图（或视野）同时被缩放"。
        //约定：
        //  · 凡是吃掉滚轮的界面件都 MarkWheelUsed()（下拉框/滑块盾、联机列表表格、聊天框...）；
        //  · 读轴的每帧路径统一放到 PostGuiWheel() 里处理, 它在 ManagerUI.OnGUI 末尾、DrawGUI 之后调用，
        //    也就是"同一个 ScrollWheel 事件趟里、所有界面件都处理完之后"，才能看到同一帧的标记
        //    （放在 Update 里读是先于 OnGUI 的，永远看不到这一帧的吃掉记录）。
        public static int WheelFrame = -1;
        public static void MarkWheelUsed() { WheelFrame = Time.frameCount; }
        public static bool WheelTakenThisFrame { get { return WheelFrame == Time.frameCount; } }

        // 任意阶段可用的"最近一次滚轮事件量"（包含 SR 面板关闭时）,  S5 修复新增。
        //  背景：Input.GetAxis("Mouse ScrollWheel") 在本环境恒为 0（_IMGUI_LESSONS.md 八.2 已定论），
        //  而既有的 WheelDelta 只在 SR 面板打开、且滚轮盾命中时才写入 ->
        //  **跑在 Update 阶段**的功能（方块选块器、聊天框缩放）在面板关着时完全拿不到滚轮。
        //  这里由**每帧总执行**的 ManagerUI.OnGUI -> PostGuiWheel 记录事件 delta（本环境唯一可靠来源），
        //  供它们跨阶段取用（Update 读到的自然是上一帧记下的值，1 帧延迟无感）。
        public static float WheelEventValue;   //向上滚为正（与 WheelDelta 同一约定）
        public static float TakeWheelEvent() { //取用即清：一次滚轮只触发一次，避免在多帧里重复生效
            try {
                float d = WheelEventValue;
                if (Mathf.Abs(d) < 0.0001f) return 0f;
                WheelEventValue = 0f;
                // **声明"这次滚轮被吃掉了"**：否则同一次滚轮会既被本功能用掉、又在下一帧的
                //  PostGuiWheel 里被 FireWheel 再分发给自由相机缩放（跨帧的重复响应）。
                MarkWheelUsed();
                return d;
            } catch { return 0f; }
        }

        //滚轮趟的统一收口：界面件之后决定这个滚轮给谁用（地图缩放 -> 自由相机缩放）
        public static void PostGuiWheel() {
            try {
                Event e = Event.current;
                if (e == null || e.type != EventType.ScrollWheel) return;   //一个滚轮事件只处理一趟
                // **S5b 修复**：改读事件 delta。原来读 Input.GetAxis（本环境恒为 0）->
                //  紧接着的 if (Mathf.Abs(wheel) < 0.0001f) return; 永远成立 ->
                //  **FireWheel 从未被调用过**：RegisterWheelHook 的唯一消费者自由相机-滚轮缩放
                //  从落地起就没生效过（注释却写着"开启后滚轮缩放视野"）。
                //  来源与 SR.Window.cs 的 WheelDeltaSet 一致（Unity 的 e.delta.y 向下滚为正 -> 取反）。
                float wheel = 0f;
                try { wheel = Mathf.Abs(e.delta.y) > 0.001f ? -e.delta.y : -e.delta.x; } catch { wheel = 0f; }
                if (WheelTakenThisFrame) return;                            //本帧已被界面件吃掉（不允许重复消费）
                if (Mathf.Abs(wheel) < 0.0001f) return;
                try { WheelEventValue = wheel; } catch { }                  // **供 Update 阶段的功能取用（面板关着也有效）**
                //滚轮分发给各功能（地图上滚轮缩放 / 自由相机缩放等）。
                //核心不知道"地图""自由相机"是什么, 谁消费、怎么消费由功能自己声明。
                if (FireWheel(wheel)) MarkWheelUsed();
            } catch (Exception __ex) { Guard.Log("滚轮仲裁", __ex); }
        }

        private static void CheckOpenKey() {
            if (_capturing != null) return;
            //只用 ComboKeyDown（= 修饰键按住 + 主键按下）；写成 GetKeyDown || ComboKeyDown 会让组合键形同虚设。
            if (SR.ComboKeyDown(_openKey)) {
                _visible = !_visible;
                if (!_visible) CloseMenu();
                else {
                    _winCollapsed = false; //always open fully expanded
                    ApplyEventSystemGate();
                }
            }
        }
        // 屏蔽游戏的 UGUI 事件系统（面板打开时防点击穿透）。EventSystem.current 在禁用期间会变 null，所以自留引用。
        // 这段门控反复改过几轮，注释一度自相矛盾（"永不禁用"和"恢复门控"并存），动它之前先看因果：
        //   · 一开始：面板开着且"屏蔽输入"就禁用 EventSystem；
        //   · 中途：发现它会让"加入房间"卡在"正在进入 / 连不上中继服务器"（加入流程本身要用 EventSystem），
        //     于是改成"永不禁用"，只把历史上被禁用过的恢复回来；
        //   · 现在：恢复门控（下面就是禁用分支），因为不禁用会有点击穿透 —— 但用 RegisterInputGateHold
        //     让"正在加入"这类流程登记挂起，挂起期间不去禁用。
        // （门控必须每帧跟随，不能只在开面板那一刻判一次，见 SR.Window.Tick 的注释。）
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

        //门控时记下 EventSystem 原本的启用状态（可逆原则：恢复时写回原值，而不是无条件置 true）
        private static bool _esGateWasEnabled = true;
        private static int _esGateScene = -1;

        private static void ApplyEventSystemGate() {
            // **P1-04**：换场景后 EventSystem 可能已经换成另一个对象（尤其是跨场景保留的那个不是当前场景的），
            //  而这里缓存的是旧引用 -> 会一直去禁用旧对象、新场景的 EventSystem 不受门控，
            //  用户侧表现为"切场景之后打开面板再也冻不住 UI / 鼠标输入"。
            //  这里按场景句柄判断：换场景就先把旧的那个按原值恢复、再清引用，下一段重新认领当前场景的。
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
                    //记原状态：游戏自己把它关掉过的话，恢复时不能擅自打开
                    _esGateWasEnabled = _gatedEventSystem != null && _gatedEventSystem.enabled;
                }
                if (_gatedEventSystem != null) _gatedEventSystem.enabled = false;
            } else {
                if (_gatedEventSystem != null && _gatedEventSystem) _gatedEventSystem.enabled = _esGateWasEnabled;
                if (_gatedEventSystem != null) _esGateWasEnabled = true;
                _gatedEventSystem = null;
            }
        }

        //冻结规则：仅冻结**自己**（hasAuthority）的角色, 树屋/大厅开面板或地图即冻结；

        internal static bool InputLocked {
            get { return (UiOpen && BlockInput) || AnyUiBlocked(); }
        }

	}

// 输入门控（独立功能类：不属于管理器框架）
// 下面 6 个补丁是打开面板/地图时冻结游戏输入这个**功能**的实现，不是管理器框架的一部分。
// 原来它们写在 partial class SR 里，于是随 SR.Core 的 CreateAndPatchAll(typeof(SR)) 一起注册
// 结果管理器自己持有一组游戏行为补丁，与"SR 只是一个空壳管理器"的方向相悖：
// 管理器不该 patch 游戏，也不该认识"角色 / 放置光标 / 键盘输入 / GameControl"这些游戏概念。
// 搬进独立 ITweak 后：由反射自动发现、自己注册；未被发现或注册失败时游戏输入完全不受影响（原版行为）。
// （放在本文件而不是新建 .cs：csproj 若为显式文件列表，新增文件不会被编译 -> 补丁静默失效。）
public class SRInputGate : ITweak {

    public void Initialize(IFeatureHost plugin) {
        try { Harmony.CreateAndPatchAll(typeof(SRInputGate)); }
        catch (Exception e) { SR.LogError("输入门控 补丁注册失败: " + e.Message); }
    }

    //冻结规则：仅冻结**自己**（hasAuthority）的角色, 树屋/大厅开面板或地图即冻结；
    //对局内仅当冻结角色开关开启时冻结（关 = 开面板/地图时自己也能动）。
    private static bool FreezeLocalCharacter(Character c) {
        if (!SR.InputLocked || !c.hasAuthority) return false;
        if (SR.Env.InTreehouse) return true; //树屋：无条件冻结自己（按需求 #11；场景判定由 SR 自带）
        return SR.PauseGame;                  //其它情况：跟随冻结角色开关
    }

    [HarmonyPatch(typeof(Character), "Update")]
    [HarmonyPrefix]
    static bool BlockGameInput(Character __instance) {
        return !FreezeLocalCharacter(__instance);
    }

    [HarmonyPatch(typeof(Character), "FixedUpdate")]
    [HarmonyPrefix]
    static bool BlockGameInputPhysics(Character __instance) {
        if (FreezeLocalCharacter(__instance)) {
            //原地冻结：防止开面板期间进行中的跳跃/冲刺把角色带过地图。
            Rigidbody2D rb = __instance.GetComponent<Rigidbody2D>();
            if (rb != null) {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            return false;
        }
        return true;
    }

    [HarmonyPatch(typeof(PiecePlacementCursor), "Update")]
    [HarmonyPrefix]
    static bool BlockCursorInput() {
        return !SR.InputLocked;
    }

    // 不再跳过 GameControl.Update, 它是游戏的相位/倒计时**状态机**（回合开始、PLACE->PLAY、结算
    //  全部在里面）。面板打开时把它跳过，回合就完全停住（表现为"一开 SR 界面，回合开始/结算卡住"），
    //  这也正是 SR.Core.cs 里"换场景要复位全部 UI 状态"那段注释写的 "stops GameControl (countdown/power-ups freeze)"。
    //  输入拦截由 PiecePlacementCursor / KeyboardInput / Cursor.checkCameraToggle 三个前缀负责，
    //  不需要停掉整个 GameControl。
    [HarmonyPatch(typeof(GameControl), "Update")]
    [HarmonyPrefix]
    static bool BlockGameControl() {
        //只在地图编辑器打开时冻结游戏状态机（编辑地图时不想让回合推进）；
        //**面板**打开时不再冻结, 否则回合开始倒计时/PLACE->PLAY/回合末结算全部停住
        //（表现为"一开 SR 界面，回合开始和结算都卡住"，见 SR.Core.cs 里换场景那段注释）。
        //面板打开时：对局外（树屋/大厅）仍冻结游戏状态机（原行为）, 否则会干扰"加入房间"；对局内不冻结，否则回合开始/结算会卡住。
        try { return !(SR.InputLocked && !SR.Gate.InMatch); } catch { return !SR.InputLocked; }
    }

    //开面板(冻输入)/地图时屏蔽镜头跟随切换（滚轮或 LT+RT 触发 checkCameraToggle 弹窗）。
    [HarmonyPatch(typeof(Cursor), "checkCameraToggle")]
    [HarmonyPrefix]
    static bool BlockCameraToggle() {
        return !SR.InputLocked;
    }

    //开面板/地图时跳过 KeyboardInput：否则滚轮被转成 RotateLeft/Right 事件切换视角。
    [HarmonyPatch(typeof(KeyboardInput), "Update")]
    [HarmonyPrefix]
    static bool BlockKeyboardInput() {
        return !SR.InputLocked;
    }
}

}
