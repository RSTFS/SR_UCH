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
        private static ConfigEntry<KeyCode> _openKey;          // 地图开关快捷键
        private static ConfigEntry<bool> _freezeChar;          // 地图打开时冻结角色
        private static ConfigEntry<bool> _teleportWithKey;     // 允许用快捷键传送
        private static ConfigEntry<KeyCode> _teleportKey;      // 传送快捷键
        private const float BaseFov = 10f;              // 基准取景视野
        private static float _fov = BaseFov;            // 当前视野（仅运行期，滚轮调，重开归位）
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
        internal static ConfigEntry<KeyCode> OpenKeyEntry { get { return _openKey; } }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                _enabled = plugin.Config.Bind(Sec, "Map Enabled", false,
                "自由模式地图：启用后可在自由模式页打开俯视地图。");
                _freezeChar = plugin.Config.Bind(Sec, "Map Freeze Character", true,
                    "冻结角色：地图打开期间角色不移动（仅打开地图时生效）。");
                _teleportWithKey = plugin.Config.Bind(Sec, "Map Teleport Key Enabled", true,
                    "传送快捷键：允许用快捷键把角色传送到光标处（仅地图打开时）。");
                _teleportKey = plugin.Config.Bind(Sec, "Map Teleport Key", KeyCode.T,
                    "地图内传送快捷键（默认 T）：地图打开时把角色传送到光标处。");
                _openKey = plugin.Config.Bind(Sec, "Map Key", KeyCode.M,
                    "地图开关快捷键（默认 M）：在地图外的任何地方按一下打开/关闭俯视地图。");
                TweakCfg.Entry(Sec, "Map Enabled", "启用地图", "Enable map",
                    "启用自由模式地图\n打开后俯视查看整张关卡\n仅自由模式可用", "enable the freeplay map\noverhead view of the whole level\nfreeplay only");
                TweakCfg.Entry(Sec, "Map Freeze Character", "冻结角色", "Freeze character",
                    "冻结角色\n地图打开期间角色不能移动\n仅打开地图时生效", "freeze character\nstops your character while the map is open\nonly while the map is open");
                TweakCfg.Entry(Sec, "Map Teleport Key Enabled", "传送快捷键", "Teleport hotkey",
                    "传送快捷键\n地图打开时按快捷键把角色传送到光标处\n关闭后只能用右键菜单传送",
                    "teleport hotkey\nwhile the map is open, press the key to teleport to the cursor\nwhen off, use the right-click menu");
                TweakCfg.Entry(Sec, "Map Teleport Key", "传送快捷键", "Teleport key",
                    "地图内传送快捷键\n地图打开时把角色传送到光标处\n默认 T，可改绑", "in-map teleport key\nteleports to the cursor while the map is open\ndefault T, rebindable");
                TweakCfg.Entry(Sec, "Map Key", "地图快捷键", "Map hotkey",
                    "地图开关快捷键\n按一下打开/关闭俯视地图\n默认 M；右键按钮可改绑", "map open/close hotkey\nopens or closes the overhead map\ndefault M; right-click the button to rebind");
                SRApi.RegisterTick("自由模式-地图", Tick);
                SRApi.RegisterPauseProvider(ShouldPause);   // 冻结角色：与 EX 同原理
                SRApi.RegisterStandaloneDrawer(Draw);   // 二类独立窗口：主面板关闭也画
                // 快捷键：地图外任意位置按一下开关（不需要打开 SR 面板）。
                // 仅地图使用 M：按一下开/关俯视地图；不影响其它界面。
                SRApi.RegisterKey("自由模式-地图开关", _openKey, "press", delegate {
                    try { if (SRApi.GateMaster) Toggle(); } catch (Exception __mk) { SRApi.Guard.Log("地图快捷键", __mk); }
                }, typeof(FreePlayPage));
                SRApi.RegisterSceneHook(Close);
                try { SRApi.RegisterPatch(typeof(FreePlayMap)); }
                catch (Exception __h) { SRApi.Guard.Log("自由模式地图/补丁", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/初始化", __ex); }
        }

        internal static void Toggle() { if (_open) Close(); else OpenMap(); }

        internal static void OpenMap() {
            try {
                if (_enabled != null && !_enabled.Value) { Toast(SRApi.Tid("d43c86f2f2", "地图未启用（先在上面勾选）", "Map is disabled (enable it above)")); return; }
                Camera cam = GameCamera();
                if (cam == null) return;
                SaveCam(cam);
                _boundsValid = false;
                _dragOffset = Vector3.zero;
                _fov = BaseFov;
                _open = true;
                _menuOpen = false;
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/打开", __ex); }
        }

        internal static void Close() {
            try {
                if (!_open) { return; }
                _boundsValid = false;
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

        // 冻结角色：与 EX 相同原理 —— 通过核心的暂停提供者登记，走游戏原生暂停通道，
        // 由游戏自己停住本地角色（不手改刚体速度，避免与物理/网络对账打架）。
        // 只有"地图打开 + 开关勾选 + 本机是行动方"时才返回 true。
        private static bool ShouldPause() {
            try {
                return _open && _freezeChar != null && _freezeChar.Value;
            } catch (Exception) { return false; }
        }

        // 传送快捷键：地图打开时把角色放到光标处。
        private static void TickTeleportKey() {
            try {
                if (!_open || _teleportWithKey == null || !_teleportWithKey.Value) return;
                if (_teleportKey == null || _teleportKey.Value == KeyCode.None) return;
                if (!Input.GetKeyDown(_teleportKey.Value)) return;
                Camera cam = GameCamera();
                if (cam == null) return;
                Vector2 world = ScreenToWorld(cam, Event.current != null ? Event.current.mousePosition : (Vector2)Input.mousePosition);
                SpawnPointTools.TeleportLocal(world);
                Toast(SRApi.Tid("66e88ba0c0", "已传送到光标处", "Teleported to the cursor"));
            } catch (Exception __tk) { SRApi.Guard.Log("地图/传送快捷键", __tk); }
        }

        internal static ConfigEntry<KeyCode> TeleportKeyEntry { get { return _teleportKey; } }

        private static void Tick() {
            try {
                if (!_open) return;
                TickTeleportKey();
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

        private static void ApplyCamera(Camera cam) {
            ApplyCameraTo(cam, _fov);
        }

        // 俯视取景：固定 z 轴正向、按基准 FOV 反推距离（改 FOV 才会真正缩放）。
        private static void ApplyCameraTo(Camera cam, float fov) {
            float w = _max.x - _min.x, h = _max.y - _min.y;
            if (w <= 0.01f || h <= 0.01f) return;
            Vector3 center = new Vector3((_min.x + _max.x) * 0.5f, (_min.y + _max.y) * 0.5f, 0f);
            float aspect = cam.pixelRect.width / Mathf.Max(1f, cam.pixelRect.height);
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

        private const float CharacterBoxSize = 1.0f;   // 角色大致占 1 个单位

        // 世界尺寸换算成屏幕像素（透视相机按距离与 FOV）。
        private static float WorldSizeToPx(Camera cam, Vector3 world, float worldSize) {
            try {
                if (cam.orthographic) return worldSize / (2f * Mathf.Max(0.1f, cam.orthographicSize)) * Screen.height;
                float dist = Vector3.Distance(cam.transform.position, world);
                float halfH = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                return worldSize / (2f * Mathf.Max(0.01f, halfH)) * Screen.height;
            } catch (Exception) { return Sc(8f); }
        }

        // 方框描边（四条细线，比方块更轻、不遮挡）。
        private static void DrawBox(Rect r, float t) {
            if (r.width <= 1f || r.height <= 1f) return;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
        }

        // 自绘光标（游戏隐藏系统光标；地图可能在 SR 面板之上/之下，需自己画）。
        private static void DrawCursor(Event e) {
            try {
                if (e == null) return;
                Texture2D cur = SRApi.Ctl.CursorTexture;
                if (cur == null) return;
                Color c = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(e.mousePosition.x - Sc(7f), e.mousePosition.y - Sc(7f), Sc(15f), Sc(15f)), cur);
                GUI.color = c;
            } catch (Exception) { /* 光标绘制失败不影响地图 */ }
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
                Font prevFont = GUI.skin.font;
                try { if (SRApi.Ctl.UiFont != null) GUI.skin.font = SRApi.Ctl.UiFont; } catch (Exception) { /* 字体不可用时用默认 */ }
                try {
                    GUI.depth = (int)UiLayer.WorldOverlay;   // 画在 SR 主窗口之下（SR 用默认 0）：两者可同时存在、互不遮挡操作
                    // 顶栏
                    float barH = Sc(44f);
                    Rect bar = new Rect(0f, 0f, Screen.width, barH);
                    GUI.Box(bar, GUIContent.none, SRApi.Ctl.Footer);
                    GUI.Label(new Rect(Sc(10f), Sc(10f), Sc(80f), Sc(24f)), SRApi.Tid("41f8e215e9", "地图", "Map"), SRApi.Ctl.SecHeader);
                    // 提示标签给足高度（原来 20px 会截断中文字形下半部分）。
                    GUI.Label(new Rect(Sc(64f), Sc(12f), Mathf.Max(Sc(120f), Screen.width - Sc(360f)), Sc(24f)),
                        SRApi.Tid("ac090f7506", "右键拖动平移 · 滚轮缩放 · 右键操作", "right-drag to pan · wheel to zoom · right-click for actions"), SRApi.Ctl.Label);
                    // 右侧：视野读数 + 重置视图 + 关闭
                    float __fovNow = _fov;
                    GUI.Label(new Rect(Screen.width - Sc(206f), Sc(12f), Sc(84f), Sc(24f)), "FOV " + __fovNow.ToString("0.1"), SRApi.Ctl.Label);
                    if (GUI.Button(new Rect(Screen.width - Sc(126f), Sc(8f), Sc(88f), Sc(28f)), SRApi.Tid("59e9908af0", "重置视图", "Reset view"), SRApi.Ctl.Btn)) {
                        _dragOffset = Vector3.zero;
                        _fov = BaseFov;
                        Toast(SRApi.Tid("8531effcc3", "视图已重置", "View reset"));
                    }
                    if (GUI.Button(new Rect(Screen.width - Sc(36f), Sc(8f), Sc(30f), Sc(28f)), "×", SRApi.Ctl.Btn)) { Close(); return; }
                    if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil)
                        GUI.Label(new Rect(Sc(10f), Sc(48f), Screen.width - Sc(20f), Sc(24f)), _toast, SRApi.Ctl.Label);

                    // 玩家与重生点标记
                    foreach (Character c in Character.AllCharacters) {
                        if (c == null) continue;
                        Vector2 sp = WorldToScreen(cam, c.transform.position);
                        GUI.color = c.hasAuthority ? Color.white : new Color(0.4f, 0.65f, 1f, 1f);
                        GUI.DrawTexture(new Rect(sp.x - Sc(3f), sp.y - Sc(3f), Sc(6f), Sc(6f)), Texture2D.whiteTexture);
                    }
                    // 自定义重生点：角色大小的方框，当前生效点用黄色；不画序号标签。
                    int __active = SpawnPointTools.ActiveMarkerIndex();
                    for (int i = 0; i < SpawnPointTools.Count; i++) {
                        Vector3 wp = SpawnPointTools.At(i);
                        Vector2 sp = WorldToScreen(cam, wp);
                        float px = WorldSizeToPx(cam, wp, CharacterBoxSize);
                        GUI.color = (i == __active) ? new Color(1f, 0.85f, 0.2f, 1f) : new Color(0.2f, 0.9f, 0.3f, 1f);
                        DrawBox(new Rect(sp.x - px * 0.5f, sp.y - px * 0.5f, px, px), Sc(2f));
                    }
                    // 游戏原本的出生点：蓝色方框（始终显示，便于对照）。
                    if (SpawnPointTools.TryGetDefault(out Vector2 __def)) {
                        Vector2 sp = WorldToScreen(cam, new Vector3(__def.x, __def.y, 0f));
                        float px = WorldSizeToPx(cam, new Vector3(__def.x, __def.y, 0f), CharacterBoxSize);
                        GUI.color = new Color(0.35f, 0.7f, 1f, 1f);
                        DrawBox(new Rect(sp.x - px * 0.5f, sp.y - px * 0.5f, px, px), Sc(2f));
                    }
                    GUI.color = prevColor;

                    if (e == null) return;
                    // 顶栏区域不参与地图交互
                    bool inBar = e.mousePosition.y < Sc(44f);
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
                            {
                                float oldFov = _fov;
                                float newFov = Mathf.Clamp(oldFov + amt * 0.6f, 1f, 20f);
                                if (Mathf.Abs(newFov - oldFov) > 0.001f) {
                                    // 以光标为缩放中心：记录光标下的世界点，改完视野后调整拖动偏移把它拉回同一屏幕位置。
                                    Vector2 beforeWorld = ScreenToWorld(cam, e.mousePosition);
                                    _fov = newFov;

                                    ApplyCameraTo(cam, newFov);
                                    Vector2 afterScreen = WorldToScreen(cam, new Vector3(beforeWorld.x, beforeWorld.y, 0f));
                                    // 屏幕坐标 y 向下、世界 z 向上：相机往后拉，把该世界点重新对齐到光标处。
                                    // 直接用"屏幕位移 → 世界位移"的换算，方向由相机投影决定，不再手写正负号。
                                    float halfH2 = Mathf.Abs(cam.transform.position.z) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                                    float worldPerPx = 2f * halfH2 / Mathf.Max(1f, Screen.height);
                                    Vector2 d = e.mousePosition - afterScreen;
                                    _dragOffset += new Vector3(-d.x * worldPerPx, d.y * worldPerPx, 0f);
                                }
                            }
                            e.Use();
                        }
                        if (e.type == EventType.MouseDown && e.button == 1) {
                            _menuOpen = true; _menuPos = e.mousePosition; _menuWorld = ScreenToWorld(cam, e.mousePosition);
                            e.Use();
                        }
                    }
                    if (_menuOpen) DrawMenu(cam, e);
                    // 自绘光标：地图是最底层窗口时也要能看到鼠标（SR 面板里画的光标会被地图盖住）。
                    DrawCursor(e);
                } finally { GUI.color = prevColor; GUI.depth = prevDepth; GUI.skin.font = prevFont; }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/绘制", __ex); }
        }

        private static void DrawMenu(Camera cam, Event e) {
            string[] items = new string[] {
                SRApi.Tid("65590c01a0", "传送到此处", "Teleport here"),
                SRApi.Tid("936ed6f0cf", "在此添加重生点", "Add spawn point here"),
                SRApi.Tid("2adbd1ff92", "删除最近的重生点", "Remove nearest spawn point"),
                SRApi.Tid("aaaef597ba", "清空重生点", "Clear spawn points"),
                SRApi.Tid("233cd1c1f3", "关闭地图", "Close map")
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
                    case 0: SpawnPointTools.TeleportLocal(_menuWorld); Toast(SRApi.Tid("b57f18f24e", "已传送到该位置", "Teleported")); break;
                    case 1: SpawnPointTools.Add(_menuWorld); Toast(SRApi.Tid("8ec6044a48", "已添加重生点（", "Spawn point added (") + SpawnPointTools.Count + ")"); break;
                    case 2: SpawnPointTools.RemoveNearest(); Toast(SRApi.Tid("5db45efee9", "已删除最近的重生点", "Nearest spawn point removed")); break;
                    case 3: SpawnPointTools.Clear(); Toast(SRApi.Tid("aacc234341", "已清空重生点", "Spawn points cleared")); break;
                    default: Close(); break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/菜单动作", __ex); }
        }

        // 地图页附加功能（都是复选框）：冻结角色 / 传送快捷键。
        internal static void RenderExtraRows() {
            try {
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    return e.Definition.Key == "Map Freeze Character" || e.Definition.Key == "Map Teleport Key Enabled";
                });
                // 只要一个快捷键：标签 + 复选框 + 快捷键框（不再要按钮，地图内按 T 即可）。
                if (SRApi.Ctl.ActionRow(SRApi.Tid("425131255f", "传送到光标", "Teleport to cursor"),
                        SRApi.Tid("60a5097541", "地图打开时按这个快捷键把角色传送到光标位置", "press this key while the map is open to teleport to the cursor"),
                        SRApi.Tid("ec0c1c05f9", "传送", "Teleport"), _teleportKey, 0f)) { }
            } catch (Exception __re) { SRApi.Guard.Log("地图页附加行", __re); }
        }

        private static float Sc(float v) { return SRApi.Ctl.Sc(v); }
    }
}
