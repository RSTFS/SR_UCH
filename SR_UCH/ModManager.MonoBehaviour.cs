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
                }
                FovAdjust.CheckKey(); //view hotkey works in every scene (no ZoomCamera needed)
                FovAdjust.TickInput(); //wheel zoom, once per frame
                ModManager.Tick();
                ModManager.CheckOpenKey();
                ModManager.CheckMapKey();
                ModManager.ApplyView();
            }

            private void LateUpdate() {
                //applied after every other LateUpdate so the game camera control loses
                ModManager.ApplyView();
            }

            private void OnGUI() {
                ModManager.DrawGUI();
            }
        }

	}
}
