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

// ==== 分区：MonoBehaviour（ManagerUI：Update/LateUpdate/OnGUI 驱动入口）====

        private class ManagerUI : MonoBehaviour {
            private bool _startup;

            private void OnEnable() {
                Camera.onPreCull += OnPreCullView;
            }

            private void OnDisable() {
                Camera.onPreCull -= OnPreCullView;
            }

            //right before rendering: the lock/map framing always wins here (perspective cameras included)
            private static void OnPreCullView(Camera cam) {
                if (cam == null) return;
                //地图与自由相机都未激活：跳过相机查找（默认状态下的每帧开销）
                if (!_mapVisible && !FovAdjust.LockView) return;
                Camera gc = GameCamera();
                if (gc == null || cam != gc) return; //only the game camera, never UI cameras
                if (_mapVisible) {
                    ApplyMapViewOnCamera(cam);
                    return;
                }
                FovAdjust.ApplyToCamera(cam);
            }

            private void Update() {
                //first frame after all tweaks are initialized: scan plugins and apply
                //the persisted external-plugin disables (this must NOT run in Initialize,
                //because other tweaks may register their config after us)
                if (!_startup) {
                    _startup = true;
                    EnsureScanned();
                    ApplyDisabledPlugins();
                    GateAudit.Run(); //T5：启动自检（列出补丁目标 + 标出每帧方法）
                }
                FovAdjust.CheckKey(); //view hotkey works in every scene (no ZoomCamera needed)
                FovAdjust.TickInput(); //wheel zoom, once per frame
                SR.Tick();
                SR.CheckOpenKey();
                SR.CheckMapKey();
                SR.CheckToggleKeys(); //EX 页开关行的快捷键
                //相机应用已收敛（提示词第 7 节性能项：原来每帧最多 4 次）。现保留两处最可靠时机：
                //  ① Camera.onPreCull（渲染前，保证“锁定/地图取景”最后生效）
                //  ② ZoomCamera.Update 后缀（游戏刚移动相机后立刻纠正）
                //这里不再调用 SR.ApplyView()：Update/LateUpdate 的两次都被上面两处覆盖，纯属重复。
            }

            private void OnGUI() {
                SR.DrawGUI();
            }
        }

	}
}
