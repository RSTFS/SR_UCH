using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //自由相机（BuildingPlus 风格）：按键切换；开启后滚轮缩放视野（透视相机改 FOV，
    //正交相机改 orthoSize），关闭后完全恢复游戏相机。任何模式/场景可用，挑战模式对局内禁用。
    //文件名是 Camera.cs（T4 改名），类名仍保留 FovAdjust：SR 内部多处按这个名字引用
    //（FovAdjust.GameCamera / ApplyToCamera / LockView / TickInput 等），改名要同步全部引用点、
    //收益只是"文件名与类名一致"，故保持现状并在此说明（与 Level.cs 的情况不同——那里必须避开 global::Level 遮蔽）。
    public class FovAdjust : ITweak {
        private const float MinFov = 1f;
        private const float MaxFov = 32f;
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

        //供地图滚轮缩放调用（视野页"当前 FOV"读相机实际值，自动跟随）
        public static void SetFov(float v) {
            if (_fovEntry == null) return;
            _fovEntry.Value = Mathf.Clamp(v, MinFov, MaxFov);
            if (_mp != null) _mp.Config.Save();
        }

        private static void SelfReg() {
            SR.LocSec("Camera", "视野", null);
            SR.NavHide("Camera"); //并入自由模式页渲染，不单独成侧栏栏目
            SR.LocKey("Camera", "Free Camera", "自由相机", null);
            SR.LocDesc("Camera", "Free Camera", "自由相机：开启后滚轮缩放视野（FOV 1-32）；关闭后完全恢复游戏默认相机。\n任何模式/场景都可用，但挑战模式对局内自动禁用。", "Free camera: wheel zooms the view (FOV 1-32); off fully restores the game camera (works in any mode/scene, auto-disabled in Challenge matches)");
            SR.LocKey("Camera", "FOV", "视野大小", null);
            SR.LocDesc("Camera", "FOV", "视野大小（1 - 32）：数值越小拉得越近，越大看得越广（自由相机开启时生效，滚轮同步）", "FOV (1 - 32): lower zooms in, higher sees more (applies when free camera is on; wheel syncs)");
            SR.LocKey("Camera", "FOV Key", "视野快捷键", null);
            SR.LocDesc("Camera", "FOV Key", "按键切换自由相机：每次启动恢复游戏默认（任何模式/场景都可用，挑战模式对局内禁用）", "Key to toggle free camera; each game start resets to default (works anywhere, disabled in Challenge matches)");
            SR.RowSliders.Add(FovSlider); //通用条目行扩展点：本功能的 FOV 用滑块渲染
            SR.RowCompanions.Add(CameraCompanion);  //「自由相机」行右侧并排它的快捷键框（同建造的「无视碰撞」）
            SR.RowFilters.Add(CameraRowFilter);     //快捷键不再单独成行
        }

        //「自由相机」的同行同伴：它的开关键位
        private static string CameraCompanion(ConfigEntryBase e) {
            if (e.Definition.Section != "Camera") return null;
            return e.Definition.Key == "Free Camera" ? "FOV Key" : null;
        }

        //FOV Key 已并入「自由相机」行，不单独成行
        private static bool CameraRowFilter(ConfigEntryBase e) {
            return e.Definition.Section != "Camera" || e.Definition.Key != "FOV Key";
        }

        //FOV 用滑块（1-32，整数显示）
        private static bool FovSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = MinFov; max = MaxFov; fmt = "0";
            return e.Definition.Section == "Camera" && e.Definition.Key == "FOV";
        }

        public void Initialize(MainPlugin plugin) {
            SelfReg();
            _mp = plugin;
            _lockEntry = plugin.Config.Bind("Camera", "Free Camera", false, "自由相机：开启后滚轮缩放视野；关闭后完全恢复游戏默认相机（任何模式/场景都可用，挑战模式对局内自动禁用）");
            _fovEntry = plugin.Config.Bind("Camera", "FOV", 10f, new ConfigDescription(
                "视野（1 - 32）", new AcceptableValueRange<float>(1f, 32f)));
            _keyEntry = plugin.Config.Bind("Camera", "FOV Key", KeyCode.F3, "按键切换自由相机（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置；任何模式/场景都可用，挑战模式对局内禁用；每次启动恢复游戏默认）");
            SR.RegisterKey("视野-自由相机", _keyEntry, "press");
            _lockEntry.SettingChanged += OnLockChanged;
            //每次启动恢复游戏默认视角
            _lockEntry.Value = false;
            //把旧范围（2-125）遗留的存档值夹回合法区间
            if (_fovEntry.Value < MinFov || _fovEntry.Value > MaxFov) {
                _fovEntry.Value = 10f;
            }
            _mp.Config.Save();
        }

        //开启时把滑条同步到当前相机；关闭时让游戏立即重算相机（完全回到默认视角）
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
                } catch (Exception __ex) { SR.Guard.Log("地图相机强制刷新取景", __ex); }
            }
            if (_mp != null) _mp.Config.Save();
        }

        public static void CheckKey() {
            if (!SR.GateMaster) return;
            if (_keyEntry == null) return;
            //仅自由模式可用（GateModeAllows 已含 IgnoreModeLimit 豁免）
            if (!SR.GateModeAllows(SR.ModeMask.Freeplay)) return;
            if (SR.ComboKeyDown(_keyEntry)) ToggleLock();
        }

        public static void ToggleLock() {
            if (_lockEntry == null) return;
            _lockEntry.Value = !_lockEntry.Value; //触发 OnLockChanged
            MainPlugin.ModLogger.LogInfo("自定义相机: " + (_lockEntry.Value ? "开（FOV=" + FovValue + "）" : "关"));        }

        //对局开始时强制关闭，避免进入挑战模式后残留
        public static void ForceDisableLock() {
            if (_lockEntry != null && _lockEntry.Value) {
                _lockEntry.Value = false; //触发 OnLockChanged → 恢复默认相机
            }
        }

        //游戏实际的渲染相机：ZoomCamera.useCamera → GetComponent<Camera> → Camera.main
        //（FovAdjust 的自定义相机与地图页共用这一份；地图页原来有一份逐行相同的副本，已合并到这里）
        internal static Camera GameCamera() {
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
            } catch (Exception __ex) { SR.Guard.Log("获取当前 ZoomCamera", __ex); }
            return Camera.main;
        }

        public static float CurrentFov() {
            Camera cam = GameCamera();
            if (cam == null) return FovValue;
            return cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
        }

        //输入每帧只处理一次（由 ManagerUI.Update 调用，不在每个相机/钩子里跑）；滚轮缩放，相机不跟随鼠标
        public static void TickInput() {
            if (!SR.GateMaster) return;
            if (!LockView) return;
            if (SR.MapOpen) return;
            Camera cam = GameCamera();
            if (cam == null) return;

            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) >= 0.0001f) {
                if (cam.orthographic) {
                    _fovEntry.Value = Mathf.Clamp(cam.orthographicSize - wheel * 5f, MinFov, MaxFov);
                } else {
                    float mid = (MinFov + MaxFov) / 2f;
                    float ratio = cam.fieldOfView / mid;
                    //滚轮是离散事件量，不能乘 Time.deltaTime
                    float nv = cam.fieldOfView - wheel * ZoomSensitivity * ratio * 100f;
                    _fovEntry.Value = Mathf.Clamp(nv, MinFov, MaxFov);
                }
                if (_mp != null) _mp.Config.Save();
            }
        }

        //每个相机都会调用（每帧多次，必须幂等）：锁定时相机 FOV 跟随滑条值
        public static void ApplyToCamera(Camera cam) {
            if (!SR.GateMaster) return;
            if (SR.MapOpen) return; //地图编辑器优先
            //同 CheckKey：仅自由模式应用（含 IgnoreModeLimit 豁免）
            if (!SR.GateModeAllows(SR.ModeMask.Freeplay)) return;
            if (!LockView) return;
            if (cam == null) return;
            if (cam.orthographic) {
                cam.orthographicSize = Mathf.Clamp(_fovEntry.Value, MinFov, MaxFov);
            } else {
                cam.fieldOfView = Mathf.Clamp(_fovEntry.Value, MinFov, MaxFov);
            }
        }
    }
}
