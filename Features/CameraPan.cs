// 相机平移：中键拖拽平移、鼠标贴边滚动、双击中键复位（移植自 BuildingPlus 的 2D Camera Controller 思路）。
// 实现方式：给 ZoomCamera.Update 加后置补丁，在游戏算完跟随位置后叠加我们的偏移，不去改游戏的跟随逻辑。
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.camerapan", "CameraPan", "1.0")]
    public class CameraPan : ITweak {
        private const string Sec = "Experiments";
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _speed;
        private static ConfigEntry<bool> _edge;
        private static Vector2 _offset;          // 累计偏移（世界单位）
        private static bool _dragging;
        private static float _lastClickAt = -10f;

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Camera Pan", "相机平移", null);
            SRApi.LocDesc(Sec, "Camera Pan", "相机平移\n按住中键拖动画面，鼠标贴边自动滚动\n仅自由模式；双击中键复位", "Camera pan\nmiddle-drag to move the view, mouse near an edge scrolls\nfreeplay only; double middle-click resets");
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
                SRApi.RegisterSceneHook(delegate { _offset = Vector2.zero; _dragging = false; });
                if (_enabled != null) _enabled.SettingChanged += delegate { if (!_enabled.Value) _offset = Vector2.zero; };
                try { Harmony.CreateAndPatchAll(typeof(CameraPanPatch)); }
                catch (Exception __h) { SRApi.Guard.Log("相机平移/补丁注册", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/初始化", __ex); }
        }

        internal static bool Active {
            get {
                try {
                    if (_enabled == null || !_enabled.Value) return false;
                    if (!SRApi.GateMaster) return false;
                    GameSettings gs = GameSettings.GetInstance();
                    if (gs == null || gs.GameMode != GameState.GameMode.FREEPLAY) return false;
                    LobbyManager lm = LobbyManager.instance;
                    return lm != null && lm.CurrentGameController != null;
                } catch (Exception __ex) { SRApi.Guard.Log("相机平移/状态判定", __ex); return false; }
            }
        }

        internal static Vector2 Offset { get { return Active ? _offset : Vector2.zero; } }

        // 每帧读输入：中键拖动 / 贴边滚动 / 双击复位。
        private static void Tick() {
            try {
                if (!Active) { _dragging = false; return; }
                float mul = _speed != null ? Mathf.Clamp(_speed.Value, 0.2f, 4f) : 1f;

                // 双击中键复位：只认两次按下间隔 < 0.35s
                if (Input.GetMouseButtonDown(2)) {
                    if (Time.unscaledTime - _lastClickAt < 0.35f) { _offset = Vector2.zero; _lastClickAt = -10f; }
                    else _lastClickAt = Time.unscaledTime;
                }
                // 中键拖动
                if (Input.GetMouseButton(2)) {
                    _dragging = true;
                    _offset -= new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * mul * 3f;
                } else if (_dragging) {
                    _dragging = false;
                }
                // 贴边滚动
                if (_edge != null && _edge.Value) {
                    Vector3 m = Input.mousePosition;
                    float band = Mathf.Min(60f, Screen.height * 0.08f);
                    float dx = 0f, dy = 0f;
                    if (m.x <= band) dx = -1f; else if (m.x >= Screen.width - band) dx = 1f;
                    if (m.y <= band) dy = -1f; else if (m.y >= Screen.height - band) dy = 1f;
                    if (dx != 0f || dy != 0f) _offset += new Vector2(dx, dy) * mul * 6f * Time.unscaledDeltaTime;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/输入", __ex); }
        }
    }

    // 后置补丁：游戏算完跟随位置后叠加偏移（不改游戏逻辑，关闭时偏移为 0 即自然还原）。
    [HarmonyPatch(typeof(ZoomCamera), "Update")]
    internal static class CameraPanPatch {
        [HarmonyPostfix]
        private static void Postfix(ZoomCamera __instance) {
            try {
                if (__instance == null) return;
                Vector2 off = CameraPan.Offset;
                if (off.sqrMagnitude < 0.0001f) return;
                Transform t = __instance.transform;
                Vector3 p = t.position;
                t.position = new Vector3(p.x + off.x, p.y + off.y, p.z);
            } catch (Exception __ex) { SRApi.Guard.Log("相机平移/应用偏移", __ex); }
        }
    }
}
