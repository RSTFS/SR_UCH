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
public partial class ModManager {

// ==== 分区：Input（打开/关闭键 / 输入冻结 / EventSystem 门控 / 角色冻结）====

        //--- open/close ---
        private static void CheckOpenKey() {
            if (_capturing != null) return;
            if (Input.GetKeyDown(_openKey.Value) || ModManager.ComboKeyDown(_openKey)) {
                _visible = !_visible;
                if (!_visible) CloseMenu();
                else {
                    _winCollapsed = false; //always open fully expanded
                    ApplyEventSystemGate();
                }
            }
        }

        //while the manager or map editor is open and blocking, disable the game's UGUI
        //event system; EventSystem.current goes null while disabled, so keep our own reference
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

        //--- 冻结规则：仅冻结**自己**（hasAuthority）的角色，其他玩家角色照常移动。
        // - 树屋/大厅：打开面板或地图即冻结自己（原版行为：树屋里其他角色正常走动，自己停住）
        // - 对局内：仅当「冻结角色」开关开启时冻结自己（关 = 打开面板/地图时自己也能动）
        private static bool FreezeLocalCharacter(Character c) {
            if (!InputLocked || !c.hasAuthority) return false;
            if (InTreehouseLobby()) return true; //树屋：面板/地图打开默认冻结自己
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
                //freeze the local character in place so an in-progress jump/dash
                //can't carry it across the map while the manager is open
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

        //窗口打开(冻结输入)或地图打开时屏蔽"所有/本地玩家"镜头跟随切换（游戏在滚轮/
        //LT+RT 时触发 Cursor.checkCameraToggle → 弹窗"本地/全员"）
        [HarmonyPatch(typeof(Cursor), "checkCameraToggle")]
        [HarmonyPrefix]
        static bool BlockCameraToggle() {
            return !InputLocked;
        }

        //窗口打开(冻结输入)或地图打开时跳过键盘/鼠标输入处理器：
        //否则界面里滚轮会被 KeyboardInput.Update 转成 RotateLeft/Right 事件 → 切换镜头视角
        [HarmonyPatch(typeof(KeyboardInput), "Update")]
        [HarmonyPrefix]
        static bool BlockKeyboardInput() {
            return !InputLocked;
        }

        //input is locked while the manager window is open (blocking) OR the map editor is open
        private static bool InputLocked {
            get { return (UiOpen && BlockInput) || _mapVisible; }
        }

	}
}
