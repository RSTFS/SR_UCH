using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Input（打开/关闭键 / 输入冻结 / EventSystem 门控 / 角色冻结 / 滚轮仲裁）====

        //================ 滚轮仲裁（按帧） ================
        //为什么需要：IMGUI 里"谁先处理谁 Use() 吃掉事件"只对 IMGUI 事件有效；而地图缩放 / 自由相机缩放 /
        //聊天框字号缩放是用 Input.GetAxis("Mouse ScrollWheel") 读的 —— 它们看不到事件有没有被界面吃掉，
        //于是会出现"在下拉框/滑块/联机列表上滚轮，地图（或视野）同时被缩放"。
        //约定：
        //  · 凡是吃掉滚轮的界面件都 MarkWheelUsed()（下拉框/滑块盾、联机列表表格、聊天框…）；
        //  · 读轴的每帧路径统一放到 PostGuiWheel() 里处理 —— 它在 ManagerUI.OnGUI 末尾、DrawGUI 之后调用，
        //    也就是"同一个 ScrollWheel 事件趟里、所有界面件都处理完之后"，才能看到同一帧的标记
        //    （放在 Update 里读是先于 OnGUI 的，永远看不到这一帧的吃掉记录）。
        public static int WheelFrame = -1;
        public static void MarkWheelUsed() { WheelFrame = Time.frameCount; }
        public static bool WheelTakenThisFrame { get { return WheelFrame == Time.frameCount; } }

        //滚轮趟的统一收口：界面件之后决定这个滚轮给谁用（地图缩放 → 自由相机缩放）
        public static void PostGuiWheel() {
            try {
                Event e = Event.current;
                if (e == null || e.type != EventType.ScrollWheel) return;   //一个滚轮事件只处理一趟
                if (WheelTakenThisFrame) return;                            //本帧已被界面件吃掉（不允许重复消费）
                float wheel = 0f;
                try { wheel = Input.GetAxis("Mouse ScrollWheel"); } catch { wheel = 0f; }
                if (Mathf.Abs(wheel) < 0.0001f) return;
                if (Freeplay.Visible) {
                    //地图上滚轮缩放：调整自由相机 FOV（视野页"当前 FOV"跟随）
                    FovAdjust.SetFov(FovAdjust.FovValue - wheel * 3f);
                    MarkWheelUsed();
                    return;
                }
                if (FovAdjust.WheelZoom(wheel)) MarkWheelUsed();            //自由相机（内部自带门控判定）
            } catch (Exception __ex) { Guard.Log("滚轮仲裁", __ex); }
        }

        //外部模块页的开关行快捷键（无视模式限制 / 冻结角色）由外部模块自己轮询；
        //这里保留入口是为了兼容 SR 侧的调用点（当前无操作）。
        public static void CheckToggleKeys() {
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

        //屏蔽游戏的 UGUI 事件系统；EventSystem.current 禁用期间会变 null，故自留引用。
        private static void ApplyEventSystemGate() {
            if (InputLocked) {
                if (_gatedEventSystem == null || !_gatedEventSystem) {
                    _gatedEventSystem = UnityEngine.EventSystems.EventSystem.current;
                    if (_gatedEventSystem == null)
                        _gatedEventSystem = UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
                }
                if (_gatedEventSystem != null) _gatedEventSystem.enabled = false;
            } else {
                if (_gatedEventSystem != null && _gatedEventSystem) _gatedEventSystem.enabled = true;
                _gatedEventSystem = null;
            }
        }

        //冻结规则：仅冻结**自己**（hasAuthority）的角色——树屋/大厅开面板或地图即冻结；
        //对局内仅当「冻结角色」开关开启时冻结（关 = 开面板/地图时自己也能动）。
        private static bool FreezeLocalCharacter(Character c) {
            if (!InputLocked || !c.hasAuthority) return false;
            if (Freeplay.InTreehouseLobby()) return true; //树屋：面板/地图打开默认冻结自己
            return PauseGame; //对局内：跟随「冻结角色」开关
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
            return !InputLocked;
        }

        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPrefix]
        static bool BlockGameControl() {
            return !InputLocked;
        }

        //开面板(冻输入)/地图时屏蔽镜头跟随切换（滚轮或 LT+RT 触发 checkCameraToggle 弹窗）。
        [HarmonyPatch(typeof(Cursor), "checkCameraToggle")]
        [HarmonyPrefix]
        static bool BlockCameraToggle() {
            return !InputLocked;
        }

        //开面板/地图时跳过 KeyboardInput：否则滚轮被转成 RotateLeft/Right 事件切换视角。
        [HarmonyPatch(typeof(KeyboardInput), "Update")]
        [HarmonyPrefix]
        static bool BlockKeyboardInput() {
            return !InputLocked;
        }

        private static bool InputLocked {
            get { return (UiOpen && BlockInput) || Freeplay.Visible; }
        }

	}
}
