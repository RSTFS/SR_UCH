using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //自由相机 (BuildingPlus-style custom camera):
    //  - F4 (自由相机) on: the wheel zooms the camera FOV (1 - 20); turning it off
    //    restores the game's own camera completely
    //  - 任何模式/场景都可用（透视相机改 FOV，正交相机改 orthoSize）；挑战模式对局内自动禁用
    //  - FOV slider mirrors the current FOV
    //  - 自由相机 Key (default F3): toggle; every game start it is off
    //Applied from ModManager's per-frame camera hook.
    public class FovAdjust : ITweak {
        private const float MinFov = 1f;
        private const float MaxFov = 20f;
        private const float ZoomSensitivity = 7f;

        private static MainPlugin _mp;
        private static ConfigEntry<bool> _lockEntry;
        private static ConfigEntry<float> _fovEntry;
        private static ConfigEntry<KeyCode> _keyEntry;

        public static bool LockView {
            get { return _lockEntry != null && _lockEntry.Value; }
        }

        public static float FovValue {
            get { return _fovEntry != null ? _fovEntry.Value : 10f; }
        }

        //供地图滚轮缩放调用：直接调 FOV（视野页"当前 FOV"读相机实际值，自动跟随）
        public static void SetFov(float v) {
            if (_fovEntry == null) return;
            _fovEntry.Value = Mathf.Clamp(v, MinFov, MaxFov);
            if (_mp != null) _mp.Config.Save();
        }

        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            _lockEntry = plugin.Config.Bind("视野", "自由相机", false, "自由相机：开启后滚轮缩放视野；关闭后完全恢复游戏默认相机（任何模式/场景都可用，挑战模式对局内自动禁用）");
            _fovEntry = plugin.Config.Bind("视野", "FOV", 10f, new ConfigDescription(
                "视野（1 - 20）", new AcceptableValueRange<float>(1f, 20f)));
            _keyEntry = plugin.Config.Bind("视野", "FOV Key", KeyCode.F3, "按键切换自由相机（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置；任何模式/场景都可用，挑战模式对局内禁用；每次启动恢复游戏默认）");
            ModManager.RegisterKey("视野-自由相机", _keyEntry, "press");
            _lockEntry.SettingChanged += OnLockChanged;
            //every game start has the normal view
            _lockEntry.Value = false;
            //the slider range used to be 2 - 125; clamp stale saved values into 1 - 20
            if (_fovEntry.Value < MinFov || _fovEntry.Value > MaxFov) {
                _fovEntry.Value = 10f;
            }
            _mp.Config.Save();
        }

        //enable: sync the slider to the current camera (clamped); disable: let the game
        //immediately recompute its camera (fully back to the game's normal view)
        private static void OnLockChanged(object s, EventArgs e) {
            if (_lockEntry.Value) {
                Camera cam = GameCamera();
                if (cam != null) {
                    float v = cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
                    _fovEntry.Value = Mathf.Clamp(v, MinFov, MaxFov);
                }
            } else {
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        ZoomCamera zc = lm.GetCurrentZoomCamera();
                        if (zc != null) zc.ForceFrameUpdate();
                    }
                } catch { }
            }
            if (_mp != null) _mp.Config.Save();
        }

        //自由相机不再限制模式：任何模式/场景都可用

        //挑战模式对局内禁用自由相机（多人挑战/单人挑战都算；附加"无视模式限制"开启后放宽）
        private static bool InChallenge() {
            if (ModManager.IgnoreModeLimit) return false;
            try { return GameSettings.GetInstance().GameMode == GameState.GameMode.CHALLENGE; } catch { return false; }
        }

        public static void CheckKey() {
            if (!ModManager.AllEnabled) return;
            if (_keyEntry == null) return;
            if (InChallenge()) return; //挑战模式禁用
            if (ModManager.ComboKeyDown(_keyEntry)) ToggleLock();
        }

        public static void ToggleLock() {
            if (_lockEntry == null) return;
            _lockEntry.Value = !_lockEntry.Value; //fires OnLockChanged
            MainPlugin.ModLogger.LogInfo("自定义相机: " + (_lockEntry.Value ? "开（FOV=" + FovValue + "）" : "关"));        }

        //对局开始（挑战模式禁用）：强制关闭自由相机，避免进入挑战后残留
        public static void ForceDisableLock() {
            if (_lockEntry != null && _lockEntry.Value) {
                _lockEntry.Value = false; //fires OnLockChanged → 恢复默认相机
            }
        }

        //the game's actual rendering camera: ZoomCamera.useCamera first, then
        //CurrentZoomCamera/GetComponent, Camera.main as fallback
        private static Camera GameCamera() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    ZoomCamera zc = lm.GetCurrentZoomCamera();
                    if (zc != null) {
                        if (zc.useCamera != null) return zc.useCamera;
                        Camera c = zc.GetComponent<Camera>();
                        if (c != null) return c;
                    }
                }
                if (ZoomCamera.CurrentZoomCamera != null) return ZoomCamera.CurrentZoomCamera;
            } catch { }
            return Camera.main;
        }

        public static float CurrentFov() {
            Camera cam = GameCamera();
            if (cam == null) return FovValue;
            return cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
        }

        //input handling runs ONCE per frame (from ManagerUI.Update) - never per camera/per hook.
        //only the mouse wheel zooms; the camera never follows the mouse
        public static void TickInput() {
            if (!ModManager.AllEnabled) return;
            if (!LockView) return;
            if (ModManager.MapOpen) return;
            if (InChallenge()) return; //挑战模式禁用
            Camera cam = GameCamera();
            if (cam == null) return;

            //mouse wheel zoom (perspective camera -> fieldOfView, like BuildingPlus)
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) >= 0.0001f) {
                if (cam.orthographic) {
                    _fovEntry.Value = Mathf.Clamp(cam.orthographicSize - wheel * 5f, MinFov, MaxFov);
                } else {
                    float mid = (MinFov + MaxFov) / 2f;
                    float ratio = cam.fieldOfView / mid;
                    float nv = cam.fieldOfView - wheel * ZoomSensitivity * ratio * 100f * Time.deltaTime;
                    _fovEntry.Value = Mathf.Clamp(nv, MinFov, MaxFov);
                }
                if (_mp != null) _mp.Config.Save();
            }
        }

        //called from ModManager per camera (multiple times per frame, so it must be
        //idempotent): when locked, the camera's FOV mirrors the slider value
        public static void ApplyToCamera(Camera cam) {
            if (!ModManager.AllEnabled) return;
            if (ModManager.MapOpen) return; //map editor takes priority
            if (!LockView) return;
            if (InChallenge()) return; //挑战模式禁用
            if (cam == null) return;
            if (cam.orthographic) {
                cam.orthographicSize = Mathf.Clamp(_fovEntry.Value, MinFov, MaxFov);
            } else {
                cam.fieldOfView = Mathf.Clamp(_fovEntry.Value, MinFov, MaxFov);
            }
        }
    }
}
