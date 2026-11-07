// 相机平移：中键拖拽平移、鼠标贴边滚动、双击中键复位（移植自 BuildingPlus 的 2D Camera Controller 思路）。
// 关键实现约束（审查 P1-1）：游戏在 ZoomCamera.FixedUpdate 里用 center - transform.position 反推、把相机拉回目标，
// 所以"在 Update 之后叠加绝对偏移"会被下一物理步吃掉。这里改为：
//   Update(Tick)      -> 把上一帧加的偏移撤掉（游戏逻辑看到的仍是它自己的位置）
//   onPreCull(渲染前) -> 临时加偏移（只影响这一帧的画面临时）
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.camerapan", "CameraPan", "1.0")]
    public class CameraPan : ITweak {
        private const string Sec = "Experiments";
        private const float MaxOffset = 60f;          // 偏移上限（世界单位）：防止相机被拖到场景外
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _speed;
        private static ConfigEntry<bool> _edge;
        private static Vector2 _offset;               // 累计偏移
        private static Vector2 _applied;              // 当前帧已经加到相机上的偏移（下一帧 Tick 撤掉）
        private static bool _dragging;
        private static float _lastClickAt = -10f;

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Camera Pan", "相机平移", null);
            SRApi.LocDesc(Sec, "Camera Pan", "相机平移\n按住中键拖动画面，鼠标贴边自动滚动\n仅自由模式；面板打开时不生效；双击中键复位", "Camera pan\nmiddle-drag to move the view, mouse near an edge scrolls\nfreeplay only; disabled while the panel is open; double middle-click resets");
            SRApi.LocKey(Sec, "Camera Pan Speed", "平移速度", null);
            SRApi.LocDesc(Sec, "Camera Pan Speed", "平移速度\n拖动与贴边滚动的倍率\n范围 0.2-4，默认 1", "Pan speed\nmultiplier for dragging and edge scrolling\nrange 0.2-4; default 1");
            SRApi.LocKey(Sec, "Camera Edge Scroll", "贴边滚动", null);
            SRApi.LocDesc(Sec, "Camera Edge Scroll", "贴边滚动\n鼠标靠近屏幕边缘时持续平移\n关闭后只保留中键拖动", "Edge scrolling\nkeeps panning while the mouse is near a screen edge\nwhen off only middle-drag works");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Camera Pan", false, "相机平移：按住中键拖拽平移画面（仅自由模式）。");
                _speed = plugin.Config.Bind(Sec, "Camera Pan Speed", 1f, new ConfigDescription("平移速度倍率（0.2-4）", new AcceptableValueRange<float>(0.2f, 4f)));
                _edge = plugin.Config.Bind(Sec, "Camera Edge Scroll", true, "贴边滚动：鼠标靠近屏幕边缘时持续平移。");
                SRApi.RegisterTick("KEY_CAMPAN", Tick);
                SRApi.RegisterCameraView(ApplyToCamera);   // onPreCull：渲染前临时偏移
                SRApi.RegisterSceneHook(delegate { _offset = Vector2.zero; _applied = Vector2.zero; _dragging = false; });
                if (_enabled != null) _enabled.SettingChanged += delegate { if (!_enabled.Value) { _offset = Vector2.zero; } };
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/初始化", __ex); }
        }

        internal static bool Active {
            get {
                try {
                    if (_enabled == null || !_enabled.Value) return false;
                    if (!SRApi.GateMaster) return false;
                    if (SRApi.UiOpen) return false;                       // 面板打开：不抢相机
                    GameSettings gs = GameSettings.GetInstance();
                    if (gs == null || gs.GameMode != GameState.GameMode.FREEPLAY) return false;
                    LobbyManager lm = LobbyManager.instance;
                    if (lm == null || lm.CurrentGameController == null) return false;
                    return InGame();
                } catch (Exception __ex) { SRApi.Guard.Log("相机平移/状态判定", __ex); return false; }
            }
        }

        // 菜单/暂停时鼠标是自由光标：此时不算"在游戏里"，避免在菜单上拖鼠标把画面拖走。
        private static bool InGame() {
            try { return (UnityEngine.Cursor.lockState != CursorLockMode.None) || !UnityEngine.Cursor.visible; }
            catch { return true; }
        }

        internal static Vector2 Offset { get { return Active ? _offset : Vector2.zero; } }

        // 撤掉上一帧加的偏移，再读本帧输入。
        private static void Tick() {
            try {
                RestoreApplied();
                if (!Active) { _dragging = false; return; }
                float mul = _speed != null ? Mathf.Clamp(_speed.Value, 0.2f, 4f) : 1f;

                if (Input.GetMouseButtonDown(2)) {
                    if (Time.unscaledTime - _lastClickAt < 0.35f) { _offset = Vector2.zero; _lastClickAt = -10f; }
                    else _lastClickAt = Time.unscaledTime;
                }
                if (Input.GetMouseButton(2)) {
                    _dragging = true;
                    _offset -= new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * mul * 3f;
                } else if (_dragging) {
                    _dragging = false;
                }
                if (_edge != null && _edge.Value) {
                    Vector3 m = Input.mousePosition;
                    float band = Mathf.Min(60f, Screen.height * 0.08f);
                    float dx = 0f, dy = 0f;
                    if (m.x <= band) dx = -1f; else if (m.x >= Screen.width - band) dx = 1f;
                    if (m.y <= band) dy = -1f; else if (m.y >= Screen.height - band) dy = 1f;
                    if (dx != 0f || dy != 0f) _offset += new Vector2(dx, dy) * mul * 6f * Time.unscaledDeltaTime;
                }
                // 上限：防止拖出场景外（这也是用户能自己"找回画面"的保险）
                if (_offset.magnitude > MaxOffset) _offset = _offset.normalized * MaxOffset;
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/输入", __ex); }
        }

        private static void RestoreApplied() {
            if (_applied.sqrMagnitude < 0.0001f) return;
            try {
                Camera cam = CurrentCamera();
                if (cam != null) {
                    Transform t = cam.transform;
                    Vector3 p = t.position;
                    t.position = new Vector3(p.x - _applied.x, p.y - _applied.y, p.z);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/撤销偏移", __ex); }
            _applied = Vector2.zero;
        }

        private static Camera CurrentCamera() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    ZoomCamera zc = lm.GetCurrentZoomCamera();
                    if (zc != null) {
                        if (zc.useCamera != null) return zc.useCamera;
                        return zc.GetComponent<Camera>();
                    }
                }
                if (ZoomCamera.CurrentZoomCamera != null) return ZoomCamera.CurrentZoomCamera;
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/取相机", __ex); }
            return Camera.main;
        }

        // 渲染前应用：只影响这一帧的画面，游戏逻辑读到的仍是未偏移的位置。
        private static void ApplyToCamera(Camera cam) {
            try {
                if (cam == null) return;
                Vector2 off = Offset;
                if (off.sqrMagnitude < 0.0001f) return;
                if (CurrentCamera() != cam) return;    // 只处理游戏主相机
                Transform t = cam.transform;
                Vector3 p = t.position;
                t.position = new Vector3(p.x + off.x, p.y + off.y, p.z);
                _applied = off;
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/应用偏移", __ex); }
        }
    }
}
