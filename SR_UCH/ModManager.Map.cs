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

// ==== 分区：Map（俯视地图视图：相机取景 / 拖拽缩放 / 传送 / 重生点标记 / 地图窗口）====

        //M key toggles the map view (overhead camera + paused game, like BetterFreeplay);
        //the map only works in freeplay mode (附加"无视模式限制"或实验"树屋地图"开启后可放宽)
        private static void CheckMapKey() {
            if (_capturing != null) return;
            if (!AllEnabled) return; //总开关关闭：地图不可用
            if (!MapEnabled) return; //地图总开关关闭：M 键无效
            //挑战模式对局内禁用地图（多人挑战/单人挑战都算；附加"无视模式限制"开启后放宽）
            try {
                if (!ModManager.IgnoreModeLimit && GameSettings.GetInstance().GameMode == GameState.GameMode.CHALLENGE) return;
            } catch { }
            if (Input.GetKeyDown(_mapKey.Value) || ModManager.ComboKeyDown(_mapKey)) {
                //树屋/大厅打开地图受「树屋地图」选择框限制（未开启时 M 键在树屋无效）
                if (InTreehouseLobby() && !Experiments.TreehouseMap) return;
                //地图任何模式/场景都能打开（T 传送仅树屋/自由模式；O 仅自由模式）
                _mapVisible = !_mapVisible;
                _ctxOpen = false;
                if (_mapVisible) EnterMapView();
                else ExitMapView();
            }
        }

        //the game's actual rendering camera: ZoomCamera.useCamera (the camera ZoomCamera
        //really controls) first, then CurrentZoomCamera/GetComponent, Camera.main as fallback
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

        //--- overhead map view (BetterFreeplay-style: main camera; 冻结角色开关开时只冻结自己) ---
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevCursorLock;
        private static bool _savedOrthographic; //地图打开前相机的投影（树屋是正交，退出时恢复）

        private static void EnterMapView() {
            Camera cam = GameCamera();
            if (cam == null) return;
            //地图默认 FOV：树屋 5.2（更贴近树屋原机位），自由模式等其他场景 10（自由相机）
            FovAdjust.SetFov(MapInTreehouse() ? 5.2f : 10f);
            if (!_camSaved) {
                _savedCamPos = cam.transform.position;
                _savedCamRot = cam.transform.rotation;
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
            //树屋相机用自由模式的相机：地图打开时强制透视（FOV 驱动），与自由模式完全一致
            cam.orthographic = false;
            //打开时中心对准角色当前位置（拖动偏移 = 角色 - 地图中心；之后可拖拽/滚轮自由移动）
            try {
                if (!_mapBoundsValid) UpdateMapBounds();
                Vector3 player = LocalPlayerPos();
                if (player.sqrMagnitude > 0.0001f) {
                    _mapDragOffset = new Vector3(
                        player.x - (_mapMin.x + _mapMax.x) / 2f,
                        player.y - (_mapMin.y + _mapMax.y) / 2f, 0f);
                }
            } catch { }
        }

        //本地玩家的当前位置（对局 = 角色；树屋 = 选中的角色或光标）
        private static Vector3 LocalPlayerPos() {
            //对局：本地玩家控制的角色（hasAuthority 的玩家对象）
            try {
                foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                    if (c != null && c.hasAuthority) return c.transform.position;
                }
            } catch { }
            //树屋/大厅：本地 LobbyPlayer 的角色实例优先，光标其次
            //（GetLobbyPlayers 遍历，不依赖 PlayerTracker；树屋角色/光标均为网络对象）
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    foreach (LobbyPlayer lp in lm.GetLobbyPlayers()) {
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        if (lp.CharacterInstance != null) return lp.CharacterInstance.transform.position;
                    }
                    foreach (LobbyPlayer lp in lm.GetLobbyPlayers()) {
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        if (lp.CursorInstance != null) return lp.CursorInstance.transform.position;
                    }
                }
            } catch { }
            //树屋兜底：本地 LobbyCursor（hasAuthority 的光标即本地玩家控制的树屋光标）
            try {
                foreach (LobbyCursor c in UnityEngine.Object.FindObjectsOfType<LobbyCursor>()) {
                    if (c != null && c.hasAuthority) return c.transform.position;
                }
            } catch { }
            return Vector3.zero;
        }

        private static void ExitMapView() {
            if (_camSaved) {
                Camera cam = GameCamera();
                if (cam != null) {
                    cam.transform.position = _savedCamPos;
                    cam.transform.rotation = _savedCamRot;
                    cam.orthographicSize = _savedOrtho;
                    cam.orthographic = _savedOrthographic; //恢复原投影（树屋正交）
                    cam.nearClipPlane = _savedNear;
                    cam.farClipPlane = _savedFar;
                    cam.fieldOfView = _savedFov;
                }
                //timeScale 统一由 Tick 的 _pauseApplied/_pauseSavedTs 管理（地图暂停/恢复走同一套，
                //避免双保存系统在"暂停+地图"叠加时的边界错误）
                UnityEngine.Cursor.visible = _prevCursorVisible;
                UnityEngine.Cursor.lockState = _prevCursorLock;
                _camSaved = false;
            }
            _ctxOpen = false;
            _activePoint = -1;
            _mapDragActive = false;
            _mapDragMoved = false;
            _mapDragOffset = Vector3.zero;
        }

        //是否在树屋/大厅场景（精确判断：大厅控制器存在或场景名为树屋系；不含对局）
        private static bool InTreehouseLobby() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null) return true;
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                return sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || (sc != null && sc.StartsWith("Lobby_"));
            } catch {
                return false;
            }
        }

        //当前地图是否在树屋/大厅（决定地图默认 FOV：树屋 5.2，其他 10）
        private static bool MapInTreehouse() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null) return true;
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || (sc != null && sc.StartsWith("Lobby_"))) return true;
            } catch { }
            try {
                return GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY;
            } catch {
                return false;
            }
        }

        //applied right before the camera renders so the game's ZoomCamera control can't
        //override it. exactly like BetterFreeplay's editor camera: frame the whole level.
        //orthographic -> orthoSize fit; perspective (UCH's real camera) -> move back so the
        //whole map fits the FOV cone (BetterFreeplay's perspective branch)
        private static void ApplyMapViewOnCamera(Camera cam) {
            if (cam == null) return;
            cam.orthographic = false; //统一使用自由相机（透视 FOV 驱动），树屋也不例外
            if (!_mapBoundsValid) UpdateMapBounds();
            float w = _mapMax.x - _mapMin.x;
            float h = _mapMax.y - _mapMin.y;
            if (w <= 0f || h <= 0f) return;
            Vector3 center = new Vector3((_mapMin.x + _mapMax.x) / 2f, (_mapMin.y + _mapMax.y) / 2f, 0f);
            float aspect = cam.pixelRect.width / Mathf.Max(1f, cam.pixelRect.height);
            if (cam.orthographic) {
                Vector3 basePos = new Vector3(center.x, center.y, cam.transform.position.z);
                cam.transform.position = basePos + _mapDragOffset;
                float fit = Mathf.Max(h / 2f, w / 2f / Mathf.Max(0.1f, aspect)) * 1.08f;
                //正交相机（树屋等）也应用自由相机 FOV：FOV10 = 原始取景，越小越放大（与透视分支一致）
                float fov = Mathf.Clamp(FovAdjust.FovValue, 1f, 20f);
                cam.orthographicSize = Mathf.Clamp(fit * (fov / 10f), 1f, 300f);
            } else {
                //perspective: 地图使用自由相机（FOV 取自视野页配置，默认 10）。
                //滚轮缩放直接调 FovAdjust 的 FOV，视野页"当前 FOV"自动跟随。
                float fov = Mathf.Clamp(FovAdjust.FovValue, 1f, 20f);
                cam.fieldOfView = fov;
                //距离固定按基准 FOV 10 计算：改 FOV 才会真正缩放视野
                //（若用当前 fov 反推距离，视野变化会被距离抵消，滚轮无效）
                float tanBase = Mathf.Tan(10f * 0.5f * Mathf.Deg2Rad);
                float dist = Mathf.Max(h / 2f, w / 2f / Mathf.Max(0.1f, aspect)) * 1.08f
                    / Mathf.Max(tanBase, 0.001f);
                dist = Mathf.Max(dist, 5f);
                //用固定垂直方向定位：树屋相机是斜视角（forward 带 x/y 分量），
                //若用 cam.transform.forward*dist 会把中心推偏 → 打开地图角色不在中心
                Vector3 basePos = center - Vector3.forward * dist;
                cam.transform.position = basePos + _mapDragOffset;
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, dist + 100f);
                cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, 0.1f);
            }
        }

        private static Vector2 CamWorldToGui(Camera cam, Vector3 world) {
            Vector3 sp = cam.WorldToScreenPoint(world);
            return new Vector2(sp.x, Screen.height - sp.y);
        }

        private static Vector2 CamScreenToWorld(Camera cam, Vector2 gui) {
            Vector2 s = new Vector2(gui.x, Screen.height - gui.y);
            //intersect the camera ray with the z=0 plane so the click position is exact
            //(a raw ScreenToWorldPoint with distance 0 is off when the camera is rotated)
            Ray ray = cam.ScreenPointToRay(new Vector3(s.x, s.y, 0f));
            if (Mathf.Abs(ray.direction.z) > 0.0001f) {
                float t = -ray.origin.z / ray.direction.z;
                Vector3 p = ray.origin + ray.direction * t;
                return new Vector2(p.x, p.y);
            }
            Vector3 f = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, Mathf.Abs(cam.transform.position.z)));
            return new Vector2(f.x, f.y);
        }

        //world-size box centered on a point, projected to GUI pixels.
        //perspective camera: orthographicSize is meaningless, compute from the FOV + distance
        private static Rect SpawnBoxGui(Camera cam, Vector3 world, float worldSize) {
            Vector2 c = CamWorldToGui(cam, world);
            float px;
            if (cam.orthographic) {
                px = worldSize / (2f * Mathf.Max(0.1f, cam.orthographicSize)) * Screen.height;
            } else {
                float dist = Vector3.Distance(cam.transform.position, world);
                float halfH = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                px = worldSize / (2f * Mathf.Max(0.01f, halfH)) * Screen.height;
            }
            return new Rect(c.x - px / 2f, c.y - px / 2f, px, px);
        }

        //小方框描边标记（比实心块更轻，减少遮挡）：四条细线组成方框
        private static void DrawBoxOutline(Camera cam, Vector3 world, float worldSize) {
            float px;
            if (cam.orthographic) {
                px = worldSize / (2f * Mathf.Max(0.1f, cam.orthographicSize)) * Screen.height;
            } else {
                float dist = Vector3.Distance(cam.transform.position, world);
                float halfH = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                px = worldSize / (2f * Mathf.Max(0.01f, halfH)) * Screen.height;
            }
            float t = Mathf.Max(2f, Sc(2)); //线宽
            Vector2 c = CamWorldToGui(cam, world);
            Rect r = new Rect(c.x - px / 2f, c.y - px / 2f, px, px);
            Texture2D wt = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), wt);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), wt);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), wt);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), wt);
        }

        //snap a world position to the ground below (raycast straight down)
        private static Vector2 SnapToGround(Vector2 world) {
            return world; //保留签名：不再做地面吸附（重生点精确放在鼠标位置）
        }


        //the game controls its camera in ZoomCamera.Update; our postfix runs right after it
        //and wins, which makes both the map framing and the 视野调整 reliable
        //camera override: map framing or 视野锁定 (called every frame). Only the game camera
        //is touched, so UI cameras are never affected.
        public static void ApplyView() {
            //地图与自由相机都未激活：跳过相机查找（Update/LateUpdate 每帧调用）
            if (!_mapVisible && !FovAdjust.LockView) return;
            Camera cam = GameCamera();
            if (cam == null) return;
            if (_mapVisible) {
                ApplyMapViewOnCamera(cam);
                return;
            }
            FovAdjust.ApplyToCamera(cam);
        }

        [HarmonyPatch(typeof(ZoomCamera), "Update")]
        [HarmonyPostfix]
        static void ForceGameCamera(ZoomCamera __instance) {
            //applied again right after the game moved the camera (belt and braces)
            if (!_mapVisible && !FovAdjust.LockView) return; //未激活：跳过
            Camera cam = null;
            try { cam = __instance.useCamera; } catch { }
            if (cam == null) cam = __instance.GetComponent<Camera>();
            if (cam == null) return;
            if (_mapVisible) {
                ApplyMapViewOnCamera(cam);
                return;
            }
            FovAdjust.ApplyToCamera(cam);
        }

        //--- 地图 page: whole-level map with right-click teleport ---
        private static bool _mapBoundsValid;
        private static Vector2 _mapMin, _mapMax;

        private static void UpdateMapBounds() {
            _mapBoundsValid = true;
            //treehouse/大厅: 房间范围由 LevelSelectController.CameraBounds 定义（比聚合一堆对象更准）
            try {
                LevelSelectController lsc = LobbyManager.instance != null ? LobbyManager.instance.CurrentLevelSelectController : null;
                if (lsc != null && lsc.CameraBounds != null) {
                    UnityEngine.Collider2D cb = lsc.CameraBounds;
                    Bounds b = cb.bounds;
                    if (b.size.x > 0.01f && b.size.y > 0.01f) {
                        _mapMin = new Vector2(b.min.x, b.min.y);
                        _mapMax = new Vector2(b.max.x, b.max.y);
                        return;
                    }
                }
            } catch { }
            //the game's exact camera bounds for the level (like BetterFreeplay)
            Level lv = UnityEngine.Object.FindObjectOfType<Level>();
            if (lv != null) {
                try {
                    Bounds b = lv.GetCameraBounds();
                    if (b.size.x > 0.01f && b.size.y > 0.01f) {
                        _mapMin = new Vector2(b.min.x, b.min.y);
                        _mapMax = new Vector2(b.max.x, b.max.y);
                        return;
                    }
                } catch { }
            }
            //fallback: aggregate placeables + players
            _mapMin = new Vector2(float.MaxValue, float.MaxValue);
            _mapMax = new Vector2(float.MinValue, float.MinValue);
            bool any = false;
            foreach (Placeable p in Placeable.AllPlaceables) {
                if (p == null) continue;
                Collider2D col = p.GetComponentInChildren<Collider2D>();
                Bounds b = col != null ? col.bounds : new Bounds(p.transform.position, Vector3.one);
                _mapMin = Vector2.Min(_mapMin, new Vector2(b.min.x, b.min.y));
                _mapMax = Vector2.Max(_mapMax, new Vector2(b.max.x, b.max.y));
                any = true;
            }
            foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                if (c == null) continue;
                Vector2 pos = c.transform.position;
                _mapMin = Vector2.Min(_mapMin, pos);
                _mapMax = Vector2.Max(_mapMax, pos);
                any = true;
            }
            if (!any) { _mapMin = Vector2.zero; _mapMax = Vector2.one; }
            Vector2 pad = (_mapMax - _mapMin) * 0.05f + Vector2.one;
            _mapMin -= pad;
            _mapMax += pad;
        }

        //--- map view: fullscreen editor (camera framed by the ZoomCamera.Update postfix) ---
        private static void DrawMapWindow() {
            EnsureScanned();
            EnsureStyles();
            EnsureFont();
            float scale = Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f);
            _scaled = scale;
            Font prevF = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;
            Event e = Event.current;
            Camera cam = GameCamera();
            if (cam == null) { if (prevF != null) GUI.skin.font = prevF; return; }
            Color prevColor = GUI.color;
            //树屋/大厅或非自由模式：地图里没有重生点概念，左键点击直接传送自己
            //（树屋场景名实际是 TreeHouseLobby；用 LevelSelectController 存在与否判断最稳）
            bool treehouseMap = false;
            try {
                LevelSelectController lsc = LobbyManager.instance != null ? LobbyManager.instance.CurrentLevelSelectController : null;
                treehouseMap = lsc != null;
            } catch { }
            if (!treehouseMap) {
                try {
                    string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    treehouseMap = sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || sc.StartsWith("Lobby_");
                } catch { }
            }
            if (!treehouseMap) {
                try {
                    treehouseMap = GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY;
                } catch { }
            }

            //top bar
            Rect bar = new Rect(0, 0, Screen.width, Sc(26));
            GUI.Box(bar, GUIContent.none, _title);
            float tY = (Sc(26) - Sc(26)) / 2f;
            GUI.Label(new Rect(Sc(12), tY, Sc(90), Sc(26)), T("地图", "Map"), _titleLabel);
            GUI.Label(new Rect(Sc(100), tY, Screen.width - Sc(200), Sc(26)),
                _mapTeleportTarget
                    ? T("附加地图传送：左键点击或按 T 把目标传送到鼠标位置 · 按 ", "Extra map teleport: left-click or press T to move the target · ") + KeyDisplayName(_mapKey.Value) + T(" 取消", " to cancel")
                    : treehouseMap
                        ? T("左键拖拽平移 · T 传送到鼠标 · 按 ", "Drag to pan · T = teleport to cursor · ") + KeyDisplayName(_mapKey.Value) + T(" 关闭", " to close")
                        : T("左键拖拽平移 · T 传送 · O 加重生点 · 按 ", "Drag to pan · T = teleport · O = spawn point · ") + KeyDisplayName(_mapKey.Value) + T(" 关闭", " to close"), _titleMid);
            if (GUI.Button(new Rect(Screen.width - Sc(32), tY, Sc(26), Sc(26)), "✕", _btn)) {
                _mapVisible = false;
                ExitMapView();
            }
            //the overhead camera already renders the real level fullscreen
            //markers/interaction only while fully open (during the close animation the camera is restored)
            if (_mapVisible) {
            //重生点标记只在自由模式显示（树屋/附加传送模式不显示）
            bool showSpawns = !treehouseMap && !_mapTeleportTarget;
            if (showSpawns) {
                SpawnPoints.ReadDefaultSpawn();
                for (int i = 0; i < SpawnPoints.CustomPoints.Count; i++) {
                    GUI.color = i == _activePoint
                        ? new Color(1f, 0.85f, 0.2f, 1f)
                        : new Color(0.2f, 0.9f, 0.3f, 1f);
                    DrawBoxOutline(cam, SpawnPoints.CustomPoints[i], 0.7f);
                }
                if (SpawnPoints.DefaultPoint.HasValue) { //game default (cyan)
                    GUI.color = new Color(0.2f, 0.9f, 1f, 1f);
                    DrawBoxOutline(cam, SpawnPoints.DefaultPoint.Value, 0.7f);
                }
            }
            Color markColor = GUI.color;
            foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) { //players
                if (c == null) continue;
                Vector2 s = CamWorldToGui(cam, c.transform.position);
                GUI.color = c.hasAuthority ? Color.white : new Color(0.4f, 0.65f, 1f, 1f);
                GUI.DrawTexture(new Rect(s.x - Sc(3), s.y - Sc(3), Sc(6), Sc(6)), Texture2D.whiteTexture);
            }
            GUI.color = markColor;
            //left-button: drag pans the map camera; a click (press+release without moving)
            //either teleports the target (附加地图传送) or selects/places a spawn point.
            //The drag offset accumulates in world units and is added on top of the
            //center+zoom base position every frame, so wheel zoom keeps working after a drag.
            if (e.type == EventType.MouseDown && e.button == 0 && !_ctxOpen) {
                _mapDragActive = true;
                _mapDragMoved = false;
                _mapDragLastScreen = e.mousePosition;
                e.Use();
            }
            if (e.type == EventType.MouseDrag && _mapDragActive) {
                Vector2 delta = e.mousePosition - _mapDragLastScreen;
                _mapDragLastScreen = e.mousePosition;
                if (delta.magnitude > Sc(4)) _mapDragMoved = true;
                if (_mapDragMoved) {
                    //screen px -> world units at the camera's focus distance
                    float worldPerPx;
                    if (cam.orthographic) {
                        worldPerPx = 2f * cam.orthographicSize / Screen.height;
                    } else {
                        float dist = Mathf.Abs(cam.transform.position.z);
                        float halfH = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                        worldPerPx = 2f * halfH / Screen.height;
                    }
                    _mapDragOffset += new Vector3(-delta.x * worldPerPx, delta.y * worldPerPx, 0f);
                }
                e.Use();
            }
            if (e.type == EventType.MouseUp && e.button == 0 && _mapDragActive) {
                _mapDragActive = false;
                if (_mapDragMoved) { e.Use(); }
                else if (!_ctxOpen) {
                    //左键点击不做操作（普通地图传送/重生点用 T / O 键）
                }
            }
            //right-click: 吞掉事件，避免游戏退回选角色；不再弹右键菜单
            if (e.type == EventType.MouseDown && e.button == 1) {
                e.Use();
            }
            //T = 传送到鼠标位置（仅树屋/自由模式支持；其他模式地图只能看）
            //O = 在鼠标位置添加重生点（仅自由模式地图）
            //用 Input.GetKeyDown（不依赖 IMGUI 事件，timeScale=0 时也可靠）
            //Input.mousePosition 是左下原点，CamScreenToWorld 期望左上原点，故翻转 y
            if (Input.GetKeyDown(KeyCode.T)) {
                Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                Vector2 w = CamScreenToWorld(cam, mp);
                if (treehouseMap || GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY) {
                    //T 传送仅树屋/自由模式
                    SpawnPoints.TeleportLocalPlayer(w);
                    _mapToast = T("已传送到鼠标位置", "Teleported to cursor");
                    _mapToastUntil = Time.unscaledTime + 1.5f;
                }
            } else if (!treehouseMap && GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY && Input.GetKeyDown(KeyCode.O)) {
                Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                Vector2 w = CamScreenToWorld(cam, mp); //精确放在鼠标位置（不做地面吸附）
                SpawnPoints.SetPoint(w);
                _activePoint = SpawnPoints.CustomPoints.Count - 1;
                _mapToast = T("已添加重生点（" + SpawnPoints.CustomPoints.Count + "）", "Spawn point added (" + SpawnPoints.CustomPoints.Count + ")");
                _mapToastUntil = Time.unscaledTime + 1.5f;
            }
            //操作提示（暂停时也可见）
            if (_mapToast.Length > 0 && Time.unscaledTime < _mapToastUntil) {
                GUI.color = new Color(1f, 1f, 0.5f, 1f);
                GUI.Label(new Rect(Sc(12), Sc(30), Screen.width - Sc(24), Sc(26)), _mapToast, _titleMid);
                GUI.color = markColor;
            }
            } //end _mapVisible-only section
            //fake mouse cursor
            GUI.DrawTexture(new Rect(e.mousePosition.x - Sc(7), e.mousePosition.y - Sc(7), Sc(15), Sc(15)), _cursorTex);
            GUI.color = prevColor;
            if (prevF != null) GUI.skin.font = prevF;
        }

	}
}
