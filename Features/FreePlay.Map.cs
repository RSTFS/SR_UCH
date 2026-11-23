// 自由模式地图：把整张关卡俯视铺满屏幕，供平移 / 缩放 / 传送 / 管理重生点。
//
// 与核心的关系：设置项与页面行全部走 SR 统一入口（TweakCfg.Entry 注册文案、
// RenderSectionEntries 渲染、ActionRow 画快捷键行），本文件只管覆盖层绘制与交互。
//
// 三条内部约定（都由下面代码结构保证，不靠注释约束）：
//   1. 相机保存与恢复成对：恢复用的是保存时那台相机实例，不会因为重新查找取到别的对象；
//   2. 屏幕↔世界换算只有 ToWorld/ToScreen/WorldPerPixel 三个函数，拖动与缩放共用；
//   3. 顶栏高度只有一个来源 BarHeight()，绘制与"是否点在顶栏"共用，不会再对不上。

using System;
using BepInEx.Configuration;
using HarmonyLib;
using SR_UCH.Tweaks;
using UnityEngine;

namespace SR_UCH {
    internal static class FreePlayMap {
        private const string Sec = "自由模式";
        private const float BaseFov = 10f;         // 基准视野：整张关卡恰好铺满
        private const float MinFov = 1f, MaxFov = 20f;
        private const float MarkerWorldSize = 1.0f;   // 角色大致占 1 个世界单位

        // ---- 配置 ----
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _openKey;
        private static ConfigEntry<bool> _freezeChar;
        private static ConfigEntry<bool> _teleportWithKey;
        private static ConfigEntry<KeyCode> _teleportKey;

        // ---- 运行期 ----
        private static bool _open;
        private static float _fov = BaseFov;
        private static Vector3 _pan;               // 相对取景中心的平移量（世界单位）
        private static bool _boundsValid;
        private static Vector2 _min, _max;
        private static bool _dragging, _dragMoved;
        private static Vector2 _dragLast;
        private static bool _menuOpen;
        private static Vector2 _menuPos, _menuWorld;
        private static float _toastUntil;
        private static string _toast = "";

        // ---- 相机快照：连同实例一起记住，恢复时不重新查找 ----
        private static Camera _cam;
        private static Vector3 _savedPos;
        private static Quaternion _savedRot;
        private static float _savedOrtho, _savedNear, _savedFar, _savedFov;
        private static bool _savedOrthographic;
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevCursorLock;
        private static bool _camSaved;

        internal static bool IsOpen { get { return _open; } }
        internal static ConfigEntry<KeyCode> OpenKeyEntry { get { return _openKey; } }

        // ================= 初始化 =================

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
                    "启用自由模式地图\n打开后俯视查看整张关卡\n仅自由模式可用",
                    "enable the freeplay map\noverhead view of the whole level\nfreeplay only");
                TweakCfg.Entry(Sec, "Map Freeze Character", "冻结角色", "Freeze character",
                    "冻结角色\n地图打开期间角色不能移动\n仅打开地图时生效",
                    "freeze character\nstops your character while the map is open\nonly while the map is open");
                TweakCfg.Entry(Sec, "Map Teleport Key Enabled", "传送快捷键", "Teleport hotkey",
                    "传送快捷键\n地图打开时按快捷键把角色传送到光标处\n关闭后只能用右键菜单传送",
                    "teleport hotkey\nwhile the map is open, press the key to teleport to the cursor\nwhen off, use the right-click menu");
                TweakCfg.Entry(Sec, "Map Teleport Key", "传送快捷键", "Teleport key",
                    "地图内传送快捷键\n地图打开时把角色传送到光标处\n默认 T，可改绑",
                    "in-map teleport key\nteleports to the cursor while the map is open\ndefault T, rebindable");
                TweakCfg.Entry(Sec, "Map Key", "地图快捷键", "Map hotkey",
                    "地图开关快捷键\n按一下打开/关闭俯视地图\n默认 M",
                    "map open/close hotkey\nopens or closes the overhead map\ndefault M");

                SRApi.RegisterTick("自由模式-地图", Tick);
                SRApi.RegisterPauseProvider(ShouldPause);
                SRApi.RegisterStandaloneDrawer(Draw);
                SRApi.RegisterKey("自由模式-地图开关", _openKey, "press", delegate {
                    try { if (SRApi.GateMaster && SRApi.FeatureAllowed(typeof(FreePlayPage))) Toggle(); }
                    catch (Exception __mk) { SRApi.Guard.Log("地图快捷键", __mk); }
                }, typeof(FreePlayPage));
                SRApi.RegisterSceneHook(Close);
                try { SRApi.RegisterPatch(typeof(FreePlayMap)); }
                catch (Exception __h) { SRApi.Guard.Log("自由模式地图/补丁", __h); }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/初始化", __ex); }
        }

        // ================= 开关 =================

        internal static void Toggle() { if (_open) Close(); else OpenMap(); }

        internal static void OpenMap() {
            try {
                if (_enabled != null && !_enabled.Value) {
                    Toast(SRApi.Tid("d43c86f2f2", "地图未启用（先在上面勾选）", "Map is disabled (enable it above)"));
                    return;
                }
                Camera cam = CurrentCamera();
                if (cam == null) { Toast(SRApi.Tid("0e7a4c1d52", "找不到游戏相机", "Game camera not found")); return; }
                SaveCam(cam);
                _boundsValid = false;
                _pan = Vector3.zero;
                _fov = BaseFov;
                _menuOpen = false;
                _dragging = false;
                _open = true;
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/打开", __ex); }
        }

        internal static void Close() {
            try {
                if (!_open) return;
                _open = false;
                _menuOpen = false;
                _dragging = false;
                _boundsValid = false;
                RestoreCam();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/关闭", __ex); }
        }

        // 找当前游戏相机。打开地图时调用一次，之后快照里记着它。
        private static Camera CurrentCamera() {
            try {
                // 静态字段是 Camera；实例方法返回 ZoomCamera（另一个类型），两者分开取。
                Camera direct = ZoomCamera.CurrentZoomCamera;
                if (direct != null) return direct;
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
            _cam = cam;
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

        // 恢复用保存时那台相机：期间换过相机实例也不会还原错对象、不会卡住 _camSaved。
        private static void RestoreCam() {
            if (!_camSaved) return;
            Camera cam = _cam;
            try {
                if (cam != null) {
                    Transform t = cam.transform;
                    t.position = _savedPos;
                    t.rotation = _savedRot;
                    cam.orthographicSize = _savedOrtho;
                    cam.orthographic = _savedOrthographic;
                    cam.nearClipPlane = _savedNear;
                    cam.farClipPlane = _savedFar;
                    cam.fieldOfView = _savedFov;
                }
            } catch (Exception __rc) { SRApi.Guard.Log("自由模式地图/恢复相机", __rc); }
            UnityEngine.Cursor.visible = _prevCursorVisible;
            UnityEngine.Cursor.lockState = _prevCursorLock;
            _cam = null;
            _camSaved = false;
        }

        // 冻结角色：走核心的暂停提供者，由游戏自己停住本地角色（不手改刚体，避免与物理对账打架）。
        private static bool ShouldPause() {
            try {
                // 相机失效时不暂停：避免 Tick 还没跑到就先把角色冻住。
                if (!_open || _cam == null) return false;
                if (_freezeChar == null || !_freezeChar.Value) return false;
                return SRApi.FeatureAllowed(typeof(FreePlayPage));
            } catch (Exception) { return false; }
        }

        // ================= 每帧 =================

        private static void Tick() {
            try {
                if (!_open) return;
                // 模式门控：自由模式页整体只在自由模式可用，这里不能只靠快捷键那条路。
                if (!SRApi.FeatureAllowed(typeof(FreePlayPage))) { Close(); return; }
                // 相机被销毁（换场景/重载）时必须自动收图：否则 _open 仍为 true，
                // ShouldPause 会继续冻结角色，而 Draw 因为取不到相机什么都不画，
                // 表现为"画面正常但人动不了、地图也看不见"。
                if (_cam == null) { Close(); return; }
                TickTeleportKey();
                EnsureBounds();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/每帧", __ex); }
        }

        private static void TickTeleportKey() {
            try {
                if (_teleportWithKey == null || !_teleportWithKey.Value) return;
                if (_teleportKey == null || _teleportKey.Value == KeyCode.None) return;
                if (!Input.GetKeyDown(_teleportKey.Value)) return;
                Camera cam = _cam;
                if (cam == null) return;
                Vector2 screen = Event.current != null ? Event.current.mousePosition : (Vector2)Input.mousePosition;
                SpawnPointTools.TeleportLocal(ToWorld(cam, screen));
                Toast(SRApi.Tid("66e88ba0c0", "已传送到光标处", "Teleported to the cursor"));
            } catch (Exception __tk) { SRApi.Guard.Log("地图/传送快捷键", __tk); }
        }

        // 游戏在 ZoomCamera.Update 里改相机；后置补丁在其后再摆一次，覆盖才稳定。
        [HarmonyPatch(typeof(ZoomCamera), "Update")]
        [HarmonyPostfix]
        static void AfterZoomCameraUpdate(ZoomCamera __instance) {
            try {
                if (!_open) return;
                if (!SRApi.FeatureAllowed(typeof(FreePlayPage))) return;   // 非自由模式不改相机
                if (_cam == null) return;                     // 只用打开时记下的那台相机
                Camera cam = _cam;
                EnsureBounds();
                ApplyCamera(cam);
            } catch (Exception) { /* 补丁内静默：异常会每帧刷日志 */ }
        }

        // ================= 取景 =================

        private static void EnsureBounds() {
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
                    Vector3 pos = p.transform.position;
                    _min = Vector2.Min(_min, new Vector2(pos.x, pos.y));
                    _max = Vector2.Max(_max, new Vector2(pos.x, pos.y));
                    any = true;
                }
            } catch (Exception) { /* 聚合失败用默认范围 */ }
            if (!any) { _min = Vector2.zero; _max = Vector2.one; }
            Vector2 pad = (_max - _min) * 0.05f + Vector2.one;
            _min -= pad;
            _max += pad;
        }

        private static void ApplyCamera(Camera cam) {
            float w = _max.x - _min.x, h = _max.y - _min.y;
            if (w <= 0.01f || h <= 0.01f) return;
            Vector2 center = new Vector2((_min.x + _max.x) * 0.5f, (_min.y + _max.y) * 0.5f);
            float aspect = cam.pixelRect.width / Mathf.Max(1f, cam.pixelRect.height);
            cam.orthographic = false;
            cam.fieldOfView = _fov;
            // 基准 10° 恰好看全整张图；改 _fov 即等比缩放。
            float tanBase = Mathf.Tan(BaseFov * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max(h * 0.5f, w * 0.5f / Mathf.Max(0.1f, aspect)) * 1.08f / Mathf.Max(tanBase, 0.001f);
            dist = Mathf.Max(dist, 5f);
            cam.transform.rotation = Quaternion.identity;
            cam.transform.position = new Vector3(center.x, center.y, 0f) - Vector3.forward * dist + _pan;
            cam.farClipPlane = Mathf.Max(_savedFar, dist + 200f);
            cam.nearClipPlane = Mathf.Min(_savedNear > 0f ? _savedNear : 0.1f, 0.1f);
        }

        // ---- 屏幕 ↔ 世界：拖动与缩放共用这三个，不再各自推算 ----

        private static Vector2 ToWorld(Camera cam, Vector2 screen) {
            Ray ray = cam.ScreenPointToRay(new Vector3(screen.x, Screen.height - screen.y, 0f));
            if (Mathf.Abs(ray.direction.z) > 0.0001f) {
                float t = -ray.origin.z / ray.direction.z;
                Vector3 p = ray.origin + ray.direction * t;
                return new Vector2(p.x, p.y);
            }
            return Vector2.zero;
        }

        private static Vector2 ToScreen(Camera cam, Vector3 world) {
            Vector3 sp = cam.WorldToScreenPoint(world);
            return new Vector2(sp.x, Screen.height - sp.y);
        }

        // 当前取景下 1 像素对应多少世界单位（俯视且朝向固定，全屏一致）。
        private static float WorldPerPixel(Camera cam) {
            float halfH = Mathf.Abs(cam.transform.position.z) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return 2f * halfH / Mathf.Max(1f, Screen.height);
        }

        private static float WorldToPixels(Camera cam, float worldSize) {
            float perPx = WorldPerPixel(cam);
            return perPx > 0.00001f ? worldSize / perPx : Sc(8f);
        }

        // ================= 绘制 =================

        private static float BarHeight() { return Sc(44f); }

        internal static void Draw() {
            if (!_open) return;
            if (!SRApi.FeatureAllowed(typeof(FreePlayPage))) return;
            Camera cam = _cam;
            if (cam == null) return;

            Event e = Event.current;
            int prevDepth = GUI.depth;
            Color prevColor = GUI.color;
            Font prevFont = GUI.skin.font;
            try {
                if (SRApi.Ctl.UiFont != null) GUI.skin.font = SRApi.Ctl.UiFont;
                GUI.depth = (int)UiLayer.WorldOverlay;
                float barH = BarHeight();

                // 交互先于绘制：避免"菜单画出来了但点不到"。
                if (e != null) HandleInput(cam, e, barH);

                DrawTopBar(barH);
                DrawMarkers(cam, prevColor);
                if (_menuOpen) DrawMenu(cam, e);
                DrawCursor(e);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/绘制", __ex); }
            finally { GUI.color = prevColor; GUI.depth = prevDepth; GUI.skin.font = prevFont; }
        }

        private static void DrawTopBar(float barH) {
            GUI.Box(new Rect(0f, 0f, Screen.width, barH), GUIContent.none, SRApi.Ctl.Footer);
            GUI.Label(new Rect(Sc(10f), Sc(10f), Sc(80f), Sc(24f)),
                SRApi.Tid("41f8e215e9", "地图", "Map"), SRApi.Ctl.SecHeader);
            GUI.Label(new Rect(Sc(64f), Sc(12f), Mathf.Max(Sc(120f), Screen.width - Sc(380f)), Sc(24f)),
                SRApi.Tid("ac090f7506", "左键拖动平移 · 滚轮缩放 · 右键操作",
                    "left-drag to pan · wheel to zoom · right-click for actions"), SRApi.Ctl.Label);
            GUI.Label(new Rect(Screen.width - Sc(216f), Sc(12f), Sc(84f), Sc(24f)),
                "FOV " + _fov.ToString("0.0"), SRApi.Ctl.Label);
            if (GUI.Button(new Rect(Screen.width - Sc(136f), Sc(8f), Sc(88f), Sc(28f)),
                    SRApi.Tid("59e9908af0", "重置视图", "Reset view"), SRApi.Ctl.Btn)) {
                _pan = Vector3.zero;
                _fov = BaseFov;
                Toast(SRApi.Tid("8531effcc3", "视图已重置", "View reset"));
            }
            if (GUI.Button(new Rect(Screen.width - Sc(40f), Sc(8f), Sc(30f), Sc(28f)), "×", SRApi.Ctl.Btn)) Close();
            if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil) {
                GUI.Label(new Rect(Sc(10f), barH + Sc(4f), Screen.width - Sc(20f), Sc(24f)), _toast, SRApi.Ctl.Label);
            }
        }

        private static void DrawMarkers(Camera cam, Color restore) {
            // 角色：本机白色，其它玩家蓝色。
            try {
                foreach (Character c in Character.AllCharacters) {
                    if (c == null) continue;
                    Vector2 sp = ToScreen(cam, c.transform.position);
                    GUI.color = c.hasAuthority ? Color.white : new Color(0.4f, 0.65f, 1f, 1f);
                    GUI.DrawTexture(new Rect(sp.x - Sc(3f), sp.y - Sc(3f), Sc(6f), Sc(6f)), Texture2D.whiteTexture);
                }
            } catch (Exception __cm) { SRApi.Guard.Log("地图/角色标记", __cm); }

            // 自定义重生点：角色大小的方框；当前生效点为黄色。
            try {
                int active = SpawnPointTools.ActiveMarkerIndex();
                for (int i = 0; i < SpawnPointTools.Count; i++) {
                    Vector3 wp = SpawnPointTools.At(i);
                    Vector2 sp = ToScreen(cam, wp);
                    float px = WorldToPixels(cam, MarkerWorldSize);
                    GUI.color = (i == active) ? new Color(1f, 0.85f, 0.2f, 1f) : new Color(0.2f, 0.9f, 0.3f, 1f);
                    DrawBox(new Rect(sp.x - px * 0.5f, sp.y - px * 0.5f, px, px), Sc(2f));
                }
            } catch (Exception __smm) { SRApi.Guard.Log("地图/重生点标记", __smm); }

            // 游戏原始出生点：蓝色方框，便于对照。
            try {
                Vector2 def;
                if (SpawnPointTools.TryGetDefault(out def)) {
                    Vector3 wp = new Vector3(def.x, def.y, 0f);
                    Vector2 sp = ToScreen(cam, wp);
                    float px = WorldToPixels(cam, MarkerWorldSize);
                    GUI.color = new Color(0.35f, 0.7f, 1f, 1f);
                    DrawBox(new Rect(sp.x - px * 0.5f, sp.y - px * 0.5f, px, px), Sc(2f));
                }
            } catch (Exception __dm) { SRApi.Guard.Log("地图/出生点标记", __dm); }

            GUI.color = restore;
        }

        private static void HandleInput(Camera cam, Event e, float barH) {
            try {
                // 顶栏与菜单打开时不参与地图拖动/缩放。
                if (_menuOpen) return;
                if (e.mousePosition.y < barH) return;

                if (e.type == EventType.MouseDown && e.button == 0) {
                    _dragging = true;
                    _dragMoved = false;
                    _dragLast = e.mousePosition;
                } else if (e.type == EventType.MouseDrag && _dragging) {
                    Vector2 d = e.mousePosition - _dragLast;
                    _dragLast = e.mousePosition;
                    if (d.magnitude > Sc(3f)) _dragMoved = true;
                    if (_dragMoved) {
                        float perPx = WorldPerPixel(cam);
                        _pan += new Vector3(-d.x * perPx, d.y * perPx, 0f);
                    }
                } else if (e.type == EventType.MouseUp && e.button == 0) {
                    _dragging = false;
                }

                if (e.type == EventType.ScrollWheel) {
                    float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                    float next = Mathf.Clamp(_fov + amt * 0.6f, MinFov, MaxFov);
                    if (Mathf.Abs(next - _fov) > 0.001f) {
                        // 以光标为缩放中心：记住光标下的世界点，缩放后把平移量补回去。
                        Vector2 anchor = ToWorld(cam, e.mousePosition);
                        _fov = next;
                        ApplyCamera(cam);
                        Vector2 after = ToScreen(cam, new Vector3(anchor.x, anchor.y, 0f));
                        float perPx = WorldPerPixel(cam);
                        Vector2 d = e.mousePosition - after;
                        _pan += new Vector3(-d.x * perPx, d.y * perPx, 0f);
                    }
                    e.Use();
                }

                if (e.type == EventType.MouseDown && e.button == 1) {
                    _menuOpen = true;
                    _menuPos = e.mousePosition;
                    _menuWorld = ToWorld(cam, e.mousePosition);
                    e.Use();
                }
            } catch (Exception __hi) { SRApi.Guard.Log("自由模式地图/交互", __hi); }
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
                if (GUI.Button(new Rect(box.x + Sc(3f), box.y + Sc(3f) + i * ih, box.width - Sc(6f), ih),
                        items[i], SRApi.Ctl.Btn)) {
                    DoMenu(i, e);
                    return;
                }
            }
            if (e != null && e.type == EventType.MouseDown && !box.Contains(e.mousePosition)) {
                _menuOpen = false;
                e.Use();
            }
        }

        private static void DoMenu(int i, Event e) {
            try {
                switch (i) {
                    case 0:
                        SpawnPointTools.TeleportLocal(_menuWorld);
                        Toast(SRApi.Tid("b57f18f24e", "已传送到该位置", "Teleported"));
                        break;
                    case 1:
                        SpawnPointTools.Add(_menuWorld);
                        Toast(SRApi.Tid("8ec6044a48", "已添加重生点（", "Spawn point added (") + SpawnPointTools.Count + ")");
                        break;
                    case 2:
                        SpawnPointTools.RemoveNearest();
                        Toast(SRApi.Tid("5db45efee9", "已删除最近的重生点", "Nearest spawn point removed"));
                        break;
                    case 3:
                        SpawnPointTools.Clear();
                        Toast(SRApi.Tid("aacc234341", "已清空重生点", "Spawn points cleared"));
                        break;
                    default:
                        Close();
                        break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式地图/菜单动作", __ex); }
            finally { _menuOpen = false; if (e != null) e.Use(); }
        }

        // ================= 小组件 =================

        private static void DrawBox(Rect r, float t) {
            if (r.width <= 1f || r.height <= 1f) return;
            Texture2D px = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), px);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), px);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), px);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), px);
        }

        // 游戏隐藏系统光标，地图需自己画。
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

        private static void Toast(string s) {
            _toast = s ?? "";
            _toastUntil = Time.unscaledTime + 2f;
        }

        // ================= 设置页附加行 =================

        internal static void RenderExtraRows() {
            try {
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    return e.Definition.Key == "Map Freeze Character" || e.Definition.Key == "Map Teleport Key Enabled";
                });
                if (SRApi.Ctl.ActionRow(
                        SRApi.Tid("425131255f", "传送到光标", "Teleport to cursor"),
                        SRApi.Tid("60a5097541", "地图打开时按这个快捷键把角色传送到光标位置",
                            "press this key while the map is open to teleport to the cursor"),
                        SRApi.Tid("ec0c1c05f9", "传送", "Teleport"), _teleportKey, 0f)) {
                    try {
                        Camera cam = _cam;
                        if (_open && cam != null) {
                            SpawnPointTools.TeleportLocal(ToWorld(cam, Input.mousePosition));
                            Toast(SRApi.Tid("66e88ba0c0", "已传送到光标处", "Teleported to the cursor"));
                        }
                    } catch (Exception __tp) { SRApi.Guard.Log("自由模式地图/页面传送", __tp); }
                }
            } catch (Exception __re) { SRApi.Guard.Log("地图页附加行", __re); }
        }

        private static float Sc(float v) { return SRApi.Ctl.Sc(v); }
    }
}
