// 自由模式 · 地图页：俯视地图视图（相机取景 / 拖拽平移 / 滚轮缩放）与右键菜单操作（传送、重生点）。
// 仅自由模式可用；右键菜单替代早期版本的 T/O 快捷键。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    internal static class FreePlayMap {
        private const string Sec = "自由模式";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _fov;
        private static bool _open;

        // 相机快照（打开时记录，关闭还原）
        private static bool _camSaved;
        private static Vector3 _savedPos;
        private static Quaternion _savedRot;
        private static float _savedOrtho, _savedNear, _savedFar, _savedFov;
        private static bool _savedOrthographic;
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevCursorLock;

        // 视野边界与拖拽
        private static bool _boundsValid;
        private static Vector2 _min, _max;
        private static Vector3 _dragOffset;
        private static bool _dragging, _dragMoved;
        private static Vector2 _dragLast;

        // 右键菜单
        private static bool _menuOpen;
        private static Vector2 _menuPos;
        private static Vector2 _menuWorld;

        internal static bool IsOpen { get { return _open; } }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                _enabled = plugin.Config.Bind(Sec, "Map Enabled", true,
                    "自由模式地图：在自由模式页按右键菜单打开/关闭俯视地图。");
                _fov = plugin.Config.Bind(Sec, "Map FOV", 10f,
                    new ConfigDescription("地图视野（1-20）：数值越小越放大。", new AcceptableValueRange<float>(1f, 20f)));
                SRApi.LocKey(Sec, "Map Enabled", "启用地图", "Enable map");
                SRApi.LocDesc(Sec, "Map Enabled",
                    "启用自由模式地图\n打开后可在自由模式页查看整张关卡平面图\n仅自由模式可用",
                    "enable the freeplay map\nshows the whole level from above\nfreeplay only");
                SRApi.LocKey(Sec, "Map FOV", "地图视野", "Map FOV");
                SRApi.LocDesc(Sec, "Map FOV",
                    "地图视野\n1-20，越小越放大\n配合滚轮缩放使用",
                    "map field of view\n1-20, smaller zooms in\nworks with the mouse wheel");
                SRApi.RegisterTick("自由模式-地图", Tick);
                SRApi.RegisterOverlay(Draw);   // 画在主窗口之上（地图是覆盖层，不是页面内容）
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/初始化", __ex); }
        }

        internal static void Toggle() { if (_open) Close(); else OpenMap(); }

        internal static void OpenMap() {
            try {
                if (_enabled != null && !_enabled.Value) return;
                if (!SRApi.FeatureAllowed(typeof(FreePlayPage))) return;   // 仅自由模式
                Camera cam = GameCamera();
                if (cam == null) return;
                SaveCam(cam);
                _boundsValid = false;
                _dragOffset = Vector3.zero;
                _open = true;
                _menuOpen = false;
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/打开", __ex); }
        }

        internal static void Close() {
            try {
                _open = false;
                _menuOpen = false;
                _dragging = false;
                RestoreCam();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/关闭", __ex); }
        }

        private static Camera GameCamera() {
            try {
                if (ZoomCamera.CurrentZoomCamera != null) return ZoomCamera.CurrentZoomCamera;
            } catch (Exception) { /* 回退到 Camera.main */ }
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
                Camera cam = GameCamera();
                if (cam == null) return;
                UpdateBounds();
                ApplyCamera(cam);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/每帧", __ex); }
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
            } catch (Exception) { /* 回退到聚合包围盒 */ }
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
            } catch (Exception) { /* 聚合失败就用默认范围 */ }
            if (!any) { _min = Vector2.zero; _max = Vector2.one; }
            Vector2 pad = (_max - _min) * 0.05f + Vector2.one;
            _min -= pad; _max += pad;
        }

        // 每帧把相机摆到俯视位置（覆盖游戏自己的相机控制）。
        private static void ApplyCamera(Camera cam) {
            float w = _max.x - _min.x, h = _max.y - _min.y;
            if (w <= 0f || h <= 0f) return;
            Vector3 center = new Vector3((_min.x + _max.x) * 0.5f, (_min.y + _max.y) * 0.5f, 0f);
            float aspect = cam.pixelRect.width / Mathf.Max(1f, cam.pixelRect.height);
            float fov = _fov != null ? Mathf.Clamp(_fov.Value, 1f, 20f) : 10f;
            cam.orthographic = false;
            cam.fieldOfView = fov;
            float tanBase = Mathf.Tan(10f * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max(h * 0.5f, w * 0.5f / Mathf.Max(0.1f, aspect)) * 1.08f / Mathf.Max(tanBase, 0.001f);
            dist = Mathf.Max(dist, 5f);
            cam.transform.position = center - Vector3.forward * dist + _dragOffset;
            cam.transform.rotation = Quaternion.identity;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, dist + 100f);
            cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, 0.1f);
        }

        private static Vector2 ScreenToWorld(Camera cam, Vector2 screen) {
            Vector3 s = new Vector3(screen.x, Screen.height - screen.y, 0f);
            Ray ray = cam.ScreenPointToRay(s);
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

        // 覆盖层绘制：标记 + 拖拽/滚轮 + 右键菜单。
        internal static void Draw() {
            if (!_open) return;
            try {
                Camera cam = GameCamera();
                if (cam == null) return;
                Event e = Event.current;
                if (e == null) return;

                // 玩家标记
                Color prev = GUI.color;
                try {
                    foreach (Character c in Character.AllCharacters) {
                        if (c == null) continue;
                        Vector2 s = WorldToScreen(cam, c.transform.position);
                        GUI.color = c.hasAuthority ? Color.white : new Color(0.4f, 0.65f, 1f, 1f);
                        GUI.DrawTexture(new Rect(s.x - Sc(3f), s.y - Sc(3f), Sc(6f), Sc(6f)), Texture2D.whiteTexture);
                    }
                    // 重生点标记
                    for (int i = 0; i < SpawnPointTools.Count; i++) {
                        Vector2 s = WorldToScreen(cam, SpawnPointTools.At(i));
                        GUI.color = new Color(0.2f, 0.9f, 0.3f, 1f);
                        GUI.DrawTexture(new Rect(s.x - Sc(4f), s.y - Sc(4f), Sc(8f), Sc(8f)), Texture2D.whiteTexture);
                    }
                } finally { GUI.color = prev; }

                // 拖拽平移
                if (!_menuOpen) {
                    if (e.type == EventType.MouseDown && e.button == 0) { _dragging = true; _dragMoved = false; _dragLast = e.mousePosition; }
                    else if (e.type == EventType.MouseDrag && _dragging) {
                        Vector2 d = e.mousePosition - _dragLast;
                        _dragLast = e.mousePosition;
                        if (d.magnitude > Sc(3f)) _dragMoved = true;
                        if (_dragMoved) {
                            float worldPerPx = 2f * Mathf.Abs(cam.transform.position.z) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, Screen.height);
                            _dragOffset += new Vector3(-d.x * worldPerPx, d.y * worldPerPx, 0f);
                        }
                    } else if (e.type == EventType.MouseUp && e.button == 0) { _dragging = false; }
                }

                // 滚轮缩放
                if (e.type == EventType.ScrollWheel && !_menuOpen) {
                    float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                    if (_fov != null) _fov.Value = Mathf.Clamp(_fov.Value + amt * 0.6f, 1f, 20f);
                    SRApi.MarkWheelUsed();
                    e.Use();
                }

                // 右键菜单
                if (e.type == EventType.MouseDown && e.button == 1 && !_dragging) {
                    _menuOpen = true;
                    _menuPos = e.mousePosition;
                    _menuWorld = ScreenToWorld(cam, e.mousePosition);
                    e.Use();
                }
                if (_menuOpen) DrawMenu(cam, e);
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
            float w = Sc(168f), ih = Sc(24f);
            Rect box = new Rect(_menuPos.x, _menuPos.y, w, ih * items.Length + Sc(6f));
            if (box.xMax > Screen.width) box.x = Screen.width - box.width;
            if (box.yMax > Screen.height) box.y = Screen.height - box.height;
            GUI.Box(box, GUIContent.none, SRApi.Ctl.Footer);
            for (int i = 0; i < items.Length; i++) {
                Rect r = new Rect(box.x + Sc(3f), box.y + Sc(3f) + i * ih, box.width - Sc(6f), ih);
                if (GUI.Button(r, items[i], SRApi.Ctl.Btn)) {
                    DoMenu(i);
                    _menuOpen = false;
                }
            }
            if (e != null && e.type == EventType.MouseDown && !box.Contains(e.mousePosition)) _menuOpen = false;
        }

        private static void DoMenu(int i) {
            try {
                switch (i) {
                    case 0: SpawnPointTools.TeleportLocal(_menuWorld); SRApi.Notify(SRApi.T("已传送到该位置", "Teleported")); break;
                    case 1: SpawnPointTools.Add(_menuWorld); SRApi.Notify(SRApi.T("已添加重生点（", "Spawn point added (") + SpawnPointTools.Count + ")"); break;
                    case 2: SpawnPointTools.RemoveNearest(); SRApi.Notify(SRApi.T("已删除最近的重生点", "Nearest spawn point removed")); break;
                    case 3: SpawnPointTools.Clear(); SRApi.Notify(SRApi.T("已清空重生点", "Spawn points cleared")); break;
                    default: Close(); break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/菜单动作", __ex); }
        }

        private static float Sc(float v) { return SRApi.Ctl.Sc(v); }
    }
}
