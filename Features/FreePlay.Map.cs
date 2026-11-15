// 自由模式 · 地图页：俯视查看整张关卡（相机取景 / 拖动平移 / 滚轮缩放 / 右键菜单操作）。
// 相机由 ZoomCamera.Update 的后置补丁强制覆盖，否则游戏每帧会把它改回去。
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    internal static class FreePlayMap {
        private const string Sec = "自由模式";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _fov;
        private static bool _open;

        // 相机快照
        private static bool _camSaved;
        private static Vector3 _savedPos;
        private static Quaternion _savedRot;
        private static float _savedOrtho, _savedNear, _savedFar, _savedFov;
        private static bool _savedOrthographic;
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevCursorLock;

        // 视图范围 / 拖动
        private static bool _boundsValid;
        private static Vector2 _min, _max;
        private static Vector3 _dragOffset;
        private static bool _dragging, _dragMoved;
        private static Vector2 _dragLast;

        // 右键菜单
        private static bool _menuOpen;
        private static Vector2 _menuPos, _menuWorld;

        private static float _toastUntil;
        private static string _toast = "";

        internal static bool IsOpen { get { return _open; } }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                _enabled = plugin.Config.Bind(Sec, "Map Enabled", false,
                "自由模式地图：启用后可在自由模式页打开俯视地图。");
                _fov = plugin.Config.Bind(Sec, "Map FOV", 10f,
                    new ConfigDescription("地图视野（1-20）：越小越放大。", new AcceptableValueRange<float>(1f, 20f)));
                SRApi.LocKey(Sec, "Map Enabled", "启用地图", "Enable map");
                SRApi.LocDesc(Sec, "Map Enabled",
                    "启用自由模式地图\n打开后俯视查看整张关卡\n仅自由模式可用",
                    "enable the freeplay map\noverhead view of the whole level\nfreeplay only");
                SRApi.LocKey(Sec, "Map FOV", "地图视野", "Map FOV");
                SRApi.LocDesc(Sec, "Map FOV",
                    "地图视野\n1-20，越小越放大\n滚轮也能缩放",
                    "map field of view\n1-20, smaller zooms in\nthe wheel also zooms");
                SRApi.RegisterTick("自由模式-地图", Tick);
                SRApi.RegisterOverlay(Draw);
                SRApi.RegisterSceneHook(Close);
                try { Harmony.CreateAndPatchAll(typeof(FreePlayMap)); }
                catch (Exception __h) { SRApi.Guard.Log("自由模式地图/补丁", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/初始化", __ex); }
        }

        internal static void Toggle() { if (_open) Close(); else OpenMap(); }

        internal static void OpenMap() {
            try {
                if (_enabled != null && !_enabled.Value) { Toast(SRApi.T("地图未启用（先在上面勾选）", "Map is disabled (enable it above)")); return; }
                if (!SRApi.FeatureAllowed(typeof(FreePlayPage))) return;
                Camera cam = GameCamera();
                if (cam == null) return;
                SaveCam(cam);
                _boundsValid = false;
                _dragOffset = Vector3.zero;
                _open = true;
                _menuOpen = false;
                Toast(SRApi.T("地图已打开：拖动平移 · 滚轮缩放 · 右键菜单", "Map open: drag to pan · wheel to zoom · right-click for actions"));
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/打开", __ex); }
        }

        internal static void Close() {
            try {
                if (!_open) { return; }
                _open = false;
                _menuOpen = false;
                _dragging = false;
                RestoreCam();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/关闭", __ex); }
        }

        internal static Camera GameCamera() {
            try {
                if (ZoomCamera.CurrentZoomCamera != null) return ZoomCamera.CurrentZoomCamera;
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    ZoomCamera zc = lm.GetCurrentZoomCamera();
                    if (zc != null) {
                        if (zc.useCamera != null) return zc.useCamera;
                        Camera c = zc.GetComponent<Camera>();
                        if (c != null) return c;
                    }
                }
            } catch (Exception) { /* 回退 Camera.main */ }
            return Camera.main;
        }

        private static void SaveCam(Camera cam) {
            if (_camSaved) return;
            _savedPos = cam.transform.position;
            _savedRot = cam.transform.rotation;
            _savedOrtho = cam.orthographicSize;
            _savedOrthographic = cam.orthographic;
            _savedNear = cam.nearClipPlane;
            _savedFar = cam.farClipPlane;
            _savedFov = cam.fieldOfView;
            _prevCursorVisible = UnityEngine.Cursor.visible;
            _prevCursorLock = UnityEngine.Cursor.lockState;
            UnityEngine.Cursor.visible = true;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            _camSaved = true;
        }

        private static void RestoreCam() {
            if (!_camSaved) return;
            Camera cam = GameCamera();
            if (cam != null) {
                cam.transform.position = _savedPos;
                cam.transform.rotation = _savedRot;
                cam.orthographicSize = _savedOrtho;
                cam.orthographic = _savedOrthographic;
                cam.nearClipPlane = _savedNear;
                cam.farClipPlane = _savedFar;
                cam.fieldOfView = _savedFov;
            }
            UnityEngine.Cursor.visible = _prevCursorVisible;
            UnityEngine.Cursor.lockState = _prevCursorLock;
            _camSaved = false;
        }

        private static void Tick() {
            try {
                if (!_open) return;
                if (!SRApi.FeatureAllowed(typeof(FreePlayPage))) { Close(); return; }
                UpdateBounds();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/每帧", __ex); }
        }

        // 游戏在 ZoomCamera.Update 里改相机；后置补丁在其后再摆一次，覆盖才稳定。
        [HarmonyPatch(typeof(ZoomCamera), "Update")]
        [HarmonyPostfix]
        static void AfterZoomCameraUpdate(ZoomCamera __instance) {
            try {
                if (!_open) return;
                Camera cam = __instance != null ? __instance.useCamera : null;
                if (cam == null && __instance != null) cam = __instance.GetComponent<Camera>();
                if (cam == null) return;
                UpdateBounds();
                ApplyCamera(cam);
            } catch (Exception) { /* 补丁内静默：异常会每帧刷日志 */ }
        }

        private static void UpdateBounds() {
            if (_boundsValid) return;
            _boundsValid = true;
            try {
                global::Level lv = UnityEngine.Object.FindObjectOfType<global::Level>();
                if (lv != null) {
                    Bounds b = lv.GetCameraBounds();
                    if (b.size.x > 0.01f && b.size.y > 0.01f) {
                        _min = new Vector2(b.min.x, b.min.y);
                        _max = new Vector2(b.max.x, b.max.y);
                        return;
                    }
                }
            } catch (Exception) { /* 回退聚合包围盒 */ }
            _min = new Vector2(float.MaxValue, float.MaxValue);
            _max = new Vector2(float.MinValue, float.MinValue);
            bool any = false;
            try {
                foreach (Placeable p in Placeable.AllPlaceables) {
                    if (p == null) continue;
                    _min = Vector2.Min(_min, (Vector2)p.transform.position);
                    _max = Vector2.Max(_max, (Vector2)p.transform.position);
                    any = true;
                }
            } catch (Exception) { /* 聚合失败用默认范围 */ }
            if (!any) { _min = Vector2.zero; _max = Vector2.one; }
            Vector2 pad = (_max - _min) * 0.05f + Vector2.one;
            _min -= pad; _max += pad;
        }

        // 俯视取景：固定 z 轴正向、按基准 FOV 反推距离（改 FOV 才会真正缩放）。
        private static void ApplyCamera(Camera cam) {
            float w = _max.x - _min.x, h = _max.y - _min.y;
            if (w <= 0.01f || h <= 0.01f) return;
            Vector3 center = new Vector3((_min.x + _max.x) * 0.5f, (_min.y + _max.y) * 0.5f, 0f);
            float aspect = cam.pixelRect.width / Mathf.Max(1f, cam.pixelRect.height);
            float fov = _fov != null ? Mathf.Clamp(_fov.Value, 1f, 20f) : 10f;
            cam.orthographic = false;
            cam.fieldOfView = fov;
            float tanBase = Mathf.Tan(10f * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max(h * 0.5f, w * 0.5f / Mathf.Max(0.1f, aspect)) * 1.08f / Mathf.Max(tanBase, 0.001f);
            dist = Mathf.Max(dist, 5f);
            cam.transform.rotation = Quaternion.identity;
            cam.transform.position = center - Vector3.forward * dist + _dragOffset;
            cam.farClipPlane = Mathf.Max(_savedFar, dist + 200f);
            cam.nearClipPlane = Mathf.Min(_savedNear > 0f ? _savedNear : 0.1f, 0.1f);
        }

        private static Vector2 ScreenToWorld(Camera cam, Vector2 screen) {
            Ray ray = cam.ScreenPointToRay(new Vector3(screen.x, Screen.height - screen.y, 0f));
            if (Mathf.Abs(ray.direction.z) > 0.0001f) {
                float t = -ray.origin.z / ray.direction.z;
                Vector3 p = ray.origin + ray.direction * t;
                return new Vector2(p.x, p.y);
            }
            return Vector2.zero;
        }

        private static Vector2 WorldToScreen(Camera cam, Vector3 world) {
            Vector3 sp = cam.WorldToScreenPoint(world);
            return new Vector2(sp.x, Screen.height - sp.y);
        }

        private static void Toast(string s) { _toast = s ?? ""; _toastUntil = Time.unscaledTime + 2f; }

        // 覆盖层：顶栏 + 标记 + 交互 + 右键菜单。画在主窗口之上。
        internal static void Draw() {
            if (!_open) return;
            try {
                Camera cam = GameCamera();
                if (cam == null) return;
                Event e = Event.current;
                int prevDepth = GUI.depth;
                Color prevColor = GUI.color;
                try {
                    GUI.depth = -900;   // 压在主窗口之上
                    // 顶栏
                    Rect bar = new Rect(0f, 0f, Screen.width, Sc(26f));
                    GUI.Box(bar, GUIContent.none, SRApi.Ctl.Footer);
                    GUI.Label(new Rect(Sc(10f), Sc(3f), Sc(80f), Sc(20f)), SRApi.T("地图", "Map"), SRApi.Ctl.SecHeader);
                    GUI.Label(new Rect(Sc(60f), Sc(4f), Screen.width - Sc(160f), Sc(20f)),
                        SRApi.T("左键拖动平移 · 滚轮缩放 · 右键操作", "drag to pan · wheel to zoom · right-click for actions"), SRApi.Ctl.Label);
                    if (GUI.Button(new Rect(Screen.width - Sc(30f), Sc(3f), Sc(24f), Sc(20f)), "×", SRApi.Ctl.Btn)) { Close(); return; }
                    if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil)
                        GUI.Label(new Rect(Sc(10f), Sc(30f), Screen.width - Sc(20f), Sc(20f)), _toast, SRApi.Ctl.Label);

                    // 玩家与重生点标记
                    foreach (Character c in Character.AllCharacters) {
                        if (c == null) continue;
                        Vector2 sp = WorldToScreen(cam, c.transform.position);
                        GUI.color = c.hasAuthority ? Color.white : new Color(0.4f, 0.65f, 1f, 1f);
                        GUI.DrawTexture(new Rect(sp.x - Sc(3f), sp.y - Sc(3f), Sc(6f), Sc(6f)), Texture2D.whiteTexture);
                    }
                    for (int i = 0; i < SpawnPointTools.Count; i++) {
                        Vector2 sp = WorldToScreen(cam, SpawnPointTools.At(i));
                        GUI.color = new Color(0.2f, 0.9f, 0.3f, 1f);
                        GUI.DrawTexture(new Rect(sp.x - Sc(4f), sp.y - Sc(4f), Sc(8f), Sc(8f)), Texture2D.whiteTexture);
                    }
                    GUI.color = prevColor;

                    if (e == null) return;
                    // 顶栏区域不参与地图交互
                    bool inBar = e.mousePosition.y < Sc(26f);
                    if (!_menuOpen && !inBar) {
                        if (e.type == EventType.MouseDown && e.button == 0) { _dragging = true; _dragMoved = false; _dragLast = e.mousePosition; }
                        else if (e.type == EventType.MouseDrag && _dragging) {
                            Vector2 d = e.mousePosition - _dragLast;
                            _dragLast = e.mousePosition;
                            if (d.magnitude > Sc(3f)) _dragMoved = true;
                            if (_dragMoved) {
                                float halfH = Mathf.Abs(cam.transform.position.z) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                                float worldPerPx = 2f * halfH / Mathf.Max(1f, Screen.height);
                                _dragOffset += new Vector3(-d.x * worldPerPx, d.y * worldPerPx, 0f);
                            }
                        } else if (e.type == EventType.MouseUp && e.button == 0) { _dragging = false; }
                        if (e.type == EventType.ScrollWheel) {
                            float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                            if (_fov != null) _fov.Value = Mathf.Clamp(_fov.Value + amt * 0.6f, 1f, 20f);
                            e.Use();
                        }
                        if (e.type == EventType.MouseDown && e.button == 1) {
                            _menuOpen = true; _menuPos = e.mousePosition; _menuWorld = ScreenToWorld(cam, e.mousePosition);
                            e.Use();
                        }
                    }
                    if (_menuOpen) DrawMenu(cam, e);
                } finally { GUI.color = prevColor; GUI.depth = prevDepth; }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/绘制", __ex); }
        }

        private static void DrawMenu(Camera cam, Event e) {
            string[] items = new string[] {
                SRApi.T("传送到此处", "Teleport here"),
                SRApi.T("在此添加重生点", "Add spawn point here"),
                SRApi.T("删除最近的重生点", "Remove nearest spawn point"),
                SRApi.T("清空重生点", "Clear spawn points"),
                SRApi.T("关闭地图", "Close map")
            };
            float w = Sc(172f), ih = Sc(24f);
            Rect box = new Rect(_menuPos.x, _menuPos.y, w, ih * items.Length + Sc(6f));
            if (box.xMax > Screen.width) box.x = Screen.width - box.width;
            if (box.yMax > Screen.height) box.y = Screen.height - box.height;
            GUI.Box(box, GUIContent.none, SRApi.Ctl.Footer);
            for (int i = 0; i < items.Length; i++) {
                if (GUI.Button(new Rect(box.x + Sc(3f), box.y + Sc(3f) + i * ih, box.width - Sc(6f), ih), items[i], SRApi.Ctl.Btn)) {
                    DoMenu(i);
                    _menuOpen = false;
                    if (e != null) e.Use();
                    return;
                }
            }
            if (e != null && e.type == EventType.MouseDown && !box.Contains(e.mousePosition)) { _menuOpen = false; e.Use(); }
        }

        private static void DoMenu(int i) {
            try {
                switch (i) {
                    case 0: SpawnPointTools.TeleportLocal(_menuWorld); Toast(SRApi.T("已传送到该位置", "Teleported")); break;
                    case 1: SpawnPointTools.Add(_menuWorld); Toast(SRApi.T("已添加重生点（", "Spawn point added (") + SpawnPointTools.Count + ")"); break;
                    case 2: SpawnPointTools.RemoveNearest(); Toast(SRApi.T("已删除最近的重生点", "Nearest spawn point removed")); break;
                    case 3: SpawnPointTools.Clear(); Toast(SRApi.T("已清空重生点", "Spawn points cleared")); break;
                    default: Close(); break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/菜单动作", __ex); }
        }

        private static float Sc(float v) { return SRApi.Ctl.Sc(v); }
    }
}
