using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public class Freeplay : ITweak {

        private static ConfigEntry<bool> _treehouseMapEntry;
        private static ConfigEntry<KeyCode> _mapKey;
        private static ConfigEntry<bool> _mapEnabledEntry; //地图总开关（地图页顶部；关闭后 M 键无法打开地图）
        private static bool _mapVisible;
        private static string _mapToast = "";
        private static float _mapToastUntil;
        private static bool _mapTeleportTarget;
        private static bool _mapDragActive;
        private static bool _mapDragMoved;
        private static Vector2 _mapDragLastScreen;
        private static Vector3 _mapDragOffset = Vector3.zero;
        private static int _activePoint = -1; //selected custom spawn point in the editor
        private static bool _camSaved;
        private static Vector3 _savedCamPos;
        private static Quaternion _savedCamRot;
        private static float _savedOrtho;
        private static float _savedNear;
        private static float _savedFar;
        private static float _savedFov; //perspective: the map view fixes FOV for a stable fit

        // 地图总开关：关闭后 M 键无效，已打开的地图立即关闭
        public static bool Enabled { get { return _mapEnabledEntry != null && _mapEnabledEntry.Value; } }
        public static bool TreehouseAllowed { get { return _treehouseMapEntry != null && _treehouseMapEntry.Value; } }
        public static bool Visible { get { return _mapVisible; } }
        internal static ConfigEntry<KeyCode> MapKeyEntry { get { return _mapKey; } }
        internal static ConfigEntry<bool> MapEnabledEntry { get { return _mapEnabledEntry; } }
        internal static ConfigEntry<bool> TreehouseMapEntry { get { return _treehouseMapEntry; } }

        // 对局开始 / 场景切换：关掉地图，并让地图边界缓存失效
        public static void Close() { if (_mapVisible) ExitMapView(); _mapVisible = false; _mapTeleportTarget = false; }
        public static void InvalidateBounds() { _mapBoundsValid = false; }

        public void Initialize(MainPlugin plugin) {
            SelfRegFreeplay();
            _mapKey = plugin.Config.Bind("Freeplay", "Map Key", KeyCode.M, "打开/关闭地图窗口（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
            SR.RegisterKey("地图-开关", _mapKey, "press");
            _mapEnabledEntry = plugin.Config.Bind("Freeplay", "Map Enabled", false, "地图总开关：关闭后无法打开地图窗口（M 键无效），已打开的地图立即关闭。\n「地图网格」等独立功能不受影响。");
            _treehouseMapEntry = plugin.Config.Bind("Freeplay", "Treehouse Map", false, "树屋地图：允许在树屋大厅使用地图（M 键开）；关闭后树屋不能开地图（地图主要用于自由模式）");
            InitGrid(plugin.Config);
            Harmony.CreateAndPatchAll(typeof(Freeplay), (string)null); //独立类不会随 SR 自动注册
        }

// ==== 分区：Freeplay（独立功能类：地图视图 + 地图网格 + 地图状态与配置）====

        //M 键开关地图视图（俯瞰相机 + 暂停游戏）；仅自由模式可用（「无视模式限制」或「树屋地图」开启后放宽）
        internal static void CheckMapKey() {
            if (SR.Ctl.IsCapturing) return;
            if (!SR.GateMaster) return; //总开关关闭：地图不可用（统一门控）
            if (!Enabled) return; //地图总开关关闭：M 键无效
            //自由模式可用；树屋需开「树屋地图」；SR.IgnoreModeLimit 由 GateModeAllows 统一豁免
            bool freeplay = SR.GateModeAllows(SR.ModeMask.Freeplay);
            bool treehouseOk = InTreehouseLobby() && TreehouseAllowed;
            if (!freeplay && !treehouseOk) return;
            //只用 ComboKeyDown（修饰键+主键，无修饰等价裸键）：曾用 GetKeyDown||ComboKeyDown，绑了 Shift+M 后单按 M 也开地图。
            if (SR.ComboKeyDown(_mapKey)) {
                //地图任何模式/场景都能打开（树屋由「地图总开关」控制；T 传送仅树屋/自由模式；O 仅自由模式）
                _mapVisible = !_mapVisible;
                if (_mapVisible) EnterMapView();
                else ExitMapView();
            }
        }

        //--- overhead map view (BetterFreeplay-style: main camera; 冻结角色开关开时只冻结自己) ---
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevCursorLock;
        private static bool _savedOrthographic; //地图打开前相机的投影（树屋是正交，退出时恢复）

        private static void EnterMapView() {
            Camera cam = FovAdjust.GameCamera();
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
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.Vector3", __ex); }
        }

        //本地玩家的当前位置（对局 = 角色；树屋 = 选中的角色或光标）
        private static Vector3 LocalPlayerPos() {
            try {
                foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                    if (c != null && c.hasAuthority) return c.transform.position;
                }
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.LocalPlayerPos", __ex); }
            //树屋/大厅：本地 LobbyPlayer 的角色实例优先，光标其次（GetLobbyPlayers 不依赖 PlayerTracker）
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
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.LocalPlayerPos", __ex); }
            try {
                foreach (LobbyCursor c in UnityEngine.Object.FindObjectsOfType<LobbyCursor>()) {
                    if (c != null && c.hasAuthority) return c.transform.position;
                }
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.LocalPlayerPos", __ex); }
            return Vector3.zero;
        }

        private static void ExitMapView() {
            if (_camSaved) {
                Camera cam = FovAdjust.GameCamera();
                if (cam != null) {
                    cam.transform.position = _savedCamPos;
                    cam.transform.rotation = _savedCamRot;
                    cam.orthographicSize = _savedOrtho;
                    cam.orthographic = _savedOrthographic; //恢复原投影（树屋正交）
                    cam.nearClipPlane = _savedNear;
                    cam.farClipPlane = _savedFar;
                    cam.fieldOfView = _savedFov;
                }
                //timeScale 统一由 Tick 的 _pauseApplied/_pauseSavedTs 管理，避免双保存系统在「暂停+地图」叠加时出错。
                UnityEngine.Cursor.visible = _prevCursorVisible;
                UnityEngine.Cursor.lockState = _prevCursorLock;
                _camSaved = false;
            }
            _activePoint = -1;
            _mapDragActive = false;
            _mapDragMoved = false;
            _mapDragOffset = Vector3.zero;
        }

        //是否在树屋/大厅场景（精确判断：大厅控制器存在或场景名为树屋系；不含对局）
        internal static bool InTreehouseLobby() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null) return true;
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                return sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || (sc != null && sc.StartsWith("Lobby_"));
            } catch {
                return false;
            }
        }

        private static bool MapInTreehouse() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null) return true;
                string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || (sc != null && sc.StartsWith("Lobby_"))) return true;
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.GetActiveScene", __ex); }
            try {
                return GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY;
            } catch {
                return false;
            }
        }

        //在相机渲染前应用，避免被游戏 ZoomCamera 覆盖：正交下用 orthoSize 贴合，透视下把相机后移让整关落入 FOV 视锥。
        internal static void ApplyMapViewOnCamera(Camera cam) {
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
                float fov = Mathf.Clamp(FovAdjust.FovValue, 1f, 60f);
                cam.orthographicSize = Mathf.Clamp(fit * (fov / 10f), 1f, 300f);
            } else {
                //地图用自由相机（FOV 取自视野页配置，默认 10）：滚轮直接调 FovAdjust，视野页"当前 FOV"自动跟随。
                float fov = Mathf.Clamp(FovAdjust.FovValue, 1f, 60f);
                cam.fieldOfView = fov;
                //距离固定按基准 FOV 10 计算：若用当前 FOV 反推距离，视野变化会被距离抵消 → 滚轮无效。
                float tanBase = Mathf.Tan(10f * 0.5f * Mathf.Deg2Rad);
                float dist = Mathf.Max(h / 2f, w / 2f / Mathf.Max(0.1f, aspect)) * 1.08f
                    / Mathf.Max(tanBase, 0.001f);
                dist = Mathf.Max(dist, 5f);
                //用固定垂直方向定位：树屋相机是斜视角，用 cam.transform.forward*dist 会把中心推偏 → 角色不在中心。
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
            //与 z=0 平面求交得到精确点击位置（相机有旋转时，距离 0 的 ScreenToWorldPoint 会偏）。
            Ray ray = cam.ScreenPointToRay(new Vector3(s.x, s.y, 0f));
            if (Mathf.Abs(ray.direction.z) > 0.0001f) {
                float t = -ray.origin.z / ray.direction.z;
                Vector3 p = ray.origin + ray.direction * t;
                return new Vector2(p.x, p.y);
            }
            Vector3 f = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, Mathf.Abs(cam.transform.position.z)));
            return new Vector2(f.x, f.y);
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
            float t = Mathf.Max(2f, SR.Ctl.Sc(2)); //线宽
            Vector2 c = CamWorldToGui(cam, world);
            Rect r = new Rect(c.x - px / 2f, c.y - px / 2f, px, px);
            Texture2D wt = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), wt);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), wt);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), wt);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), wt);
        }

        //ZoomCamera.Update 之后应用的 postfix 可压过游戏相机控制，保证地图取景与视野锁定可靠；只动游戏相机，不影响 UI 相机。
        public static void ApplyView() {
            //地图与自由相机都未激活：跳过相机查找（Update/LateUpdate 每帧调用）
            if (!_mapVisible && !FovAdjust.LockView) return;
            Camera cam = FovAdjust.GameCamera();
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
            try { cam = __instance.useCamera; } catch (Exception __ex) { SR.Guard.Log("SR.Map.ForceGameCamera", __ex); }
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
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.Vector2", __ex); }
            Level lv = UnityEngine.Object.FindObjectOfType<Level>();
            if (lv != null) {
                try {
                    Bounds b = lv.GetCameraBounds();
                    if (b.size.x > 0.01f && b.size.y > 0.01f) {
                        _mapMin = new Vector2(b.min.x, b.min.y);
                        _mapMax = new Vector2(b.max.x, b.max.y);
                        return;
                    }
                } catch (Exception __ex) { SR.Guard.Log("SR.Map.Vector2", __ex); }
            }
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
        internal static void DrawMapWindow() {
            SR.Ctl.PrepareGuiSkin();
            Color prevColor = GUI.color;
            Font prevF = SR.Ctl.PrevFont;
            Event e = Event.current;
            Camera cam = FovAdjust.GameCamera();
            if (cam == null) { if (prevF != null) GUI.skin.font = prevF; return; }
            //树屋/大厅或非自由模式：没有重生点概念，点击直接传送自己（用 LevelSelectController 判断最稳）。
            bool treehouseMap = false;
            try {
                LevelSelectController lsc = LobbyManager.instance != null ? LobbyManager.instance.CurrentLevelSelectController : null;
                treehouseMap = lsc != null;
            } catch (Exception __ex) { SR.Guard.Log("SR.Map.GameCamera", __ex); }
            if (!treehouseMap) {
                try {
                    string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    treehouseMap = sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || sc.StartsWith("Lobby_");
                } catch (Exception __ex) { SR.Guard.Log("SR.Map.StartsWith", __ex); }
            }
            if (!treehouseMap) {
                try {
                    treehouseMap = GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY;
                } catch (Exception __ex) { SR.Guard.Log("SR.Map.GetInstance", __ex); }
            }

            Rect bar = new Rect(0, 0, Screen.width, SR.Ctl.Sc(26));
            GUI.Box(bar, GUIContent.none, SR.Ctl.Title);
            float tY = (SR.Ctl.Sc(26) - SR.Ctl.Sc(26)) / 2f;
            GUI.Label(new Rect(SR.Ctl.Sc(12), tY, SR.Ctl.Sc(90), SR.Ctl.Sc(26)), SR.T("地图", "Map"), SR.Ctl.TitleLabel);
            GUI.Label(new Rect(SR.Ctl.Sc(100), tY, Screen.width - SR.Ctl.Sc(200), SR.Ctl.Sc(26)),
                _mapTeleportTarget
                    ? SR.T("附加地图传送：左键点击或按 T 把目标传送到鼠标位置 · 按 ", "Extra map teleport: left-click or press T to move the target · ") + SR.KeyDisplayName(_mapKey.Value) + SR.T(" 取消", " to cancel")
                    : treehouseMap
                        ? SR.T("左键拖拽平移 · T 传送到鼠标 · 按 ", "Drag to pan · T = teleport to cursor · ") + SR.KeyDisplayName(_mapKey.Value) + SR.T(" 关闭", " to close")
                        : SR.T("左键拖拽平移 · T 传送 · O 加重生点 · 按 ", "Drag to pan · T = teleport · O = spawn point · ") + SR.KeyDisplayName(_mapKey.Value) + SR.T(" 关闭", " to close"), SR.Ctl.TitleMid);
            if (GUI.Button(new Rect(Screen.width - SR.Ctl.Sc(32), tY, SR.Ctl.Sc(26), SR.Ctl.Sc(26)), "✕", SR.Ctl.Btn)) {
                _mapVisible = false;
                ExitMapView();
            }
            //俯瞰相机已全屏渲染真实关卡，标记/交互只在完全打开时绘制（关闭动画中相机已还原）。
            if (_mapVisible) {
            //重生点标记只在自由模式显示（树屋/附加传送模式不显示）
            bool showSpawns = !treehouseMap && !_mapTeleportTarget;
            if (showSpawns) {
                Respawn.ReadDefaultSpawn();
                for (int i = 0; i < Respawn.CustomPoints.Count; i++) {
                    GUI.color = i == _activePoint
                        ? new Color(1f, 0.85f, 0.2f, 1f)
                        : new Color(0.2f, 0.9f, 0.3f, 1f);
                    DrawBoxOutline(cam, Respawn.CustomPoints[i], 0.7f);
                }
                if (Respawn.DefaultPoint.HasValue) { //game default (cyan)
                    GUI.color = new Color(0.2f, 0.9f, 1f, 1f);
                    DrawBoxOutline(cam, Respawn.DefaultPoint.Value, 0.7f);
                }
            }
            Color markColor = GUI.color;
            foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) { //players
                if (c == null) continue;
                Vector2 s = CamWorldToGui(cam, c.transform.position);
                GUI.color = c.hasAuthority ? Color.white : new Color(0.4f, 0.65f, 1f, 1f);
                GUI.DrawTexture(new Rect(s.x - SR.Ctl.Sc(3), s.y - SR.Ctl.Sc(3), SR.Ctl.Sc(6), SR.Ctl.Sc(6)), Texture2D.whiteTexture);
            }
            GUI.color = markColor;
            //左键：拖动平移；按下+松开且未移动视为点击（附加传送 → 传送目标，否则选/放重生点）。
            //拖动偏移以世界单位累加在「居中+缩放」基准之上，拖动后滚轮缩放依然有效。
            if (e.type == EventType.MouseDown && e.button == 0) {
                _mapDragActive = true;
                _mapDragMoved = false;
                _mapDragLastScreen = e.mousePosition;
                e.Use();
            }
            if (e.type == EventType.MouseDrag && _mapDragActive) {
                Vector2 delta = e.mousePosition - _mapDragLastScreen;
                _mapDragLastScreen = e.mousePosition;
                if (delta.magnitude > SR.Ctl.Sc(4)) _mapDragMoved = true;
                if (_mapDragMoved) {
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
            }
            //right-click: 吞掉事件，避免游戏退回选角色；不再弹右键菜单
            if (e.type == EventType.MouseDown && e.button == 1) {
                e.Use();
            }
            //T = 传送到鼠标位置（仅树屋/自由模式）；O = 添加重生点（仅自由模式）。
            //用 Input.GetKeyDown 不依赖 IMGUI 事件（timeScale=0 时可靠）；mousePosition 左下原点需翻转 y。
            if (Input.GetKeyDown(KeyCode.T)) {
                Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                Vector2 w = CamScreenToWorld(cam, mp);
                if (treehouseMap || GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY) {
                    Respawn.TeleportLocalPlayer(w);
                    _mapToast = SR.T("已传送到鼠标位置", "Teleported to cursor");
                    _mapToastUntil = Time.unscaledTime + 1.5f;
                }
            } else if (!treehouseMap && GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY && Input.GetKeyDown(KeyCode.O)) {
                Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                Vector2 w = CamScreenToWorld(cam, mp); //精确放在鼠标位置（不做地面吸附）
                Respawn.SetPoint(w);
                _activePoint = Respawn.CustomPoints.Count - 1;
                _mapToast = SR.T("已添加重生点（" + Respawn.CustomPoints.Count + "）", "Spawn point added (" + Respawn.CustomPoints.Count + ")");
                _mapToastUntil = Time.unscaledTime + 1.5f;
            } else if (!treehouseMap && GameSettings.GetInstance().GameMode == GameState.GameMode.FREEPLAY && Input.GetKeyDown(KeyCode.N)) {
                //N：删除鼠标位置附近的自定义重生点（游戏默认重生点不可删）
                Vector2 mp2 = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                Vector2 w2 = CamScreenToWorld(cam, mp2);
                if (Respawn.RemoveNearest(w2, 1.5f)) {
                    _activePoint = -1;
                    _mapToast = SR.T("已删除该处重生点（剩 " + Respawn.CustomPoints.Count + "）", "Removed spawn point (" + Respawn.CustomPoints.Count + " left)");
                } else {
                    _mapToast = SR.T("此处没有自定义重生点（游戏自带起点不可删）", "No custom spawn point here (the default start can't be removed)");
                }
                _mapToastUntil = Time.unscaledTime + 1.5f;
            }
            if (_mapToast.Length > 0 && Time.unscaledTime < _mapToastUntil) {
                GUI.color = new Color(1f, 1f, 0.5f, 1f);
                GUI.Label(new Rect(SR.Ctl.Sc(12), SR.Ctl.Sc(30), Screen.width - SR.Ctl.Sc(24), SR.Ctl.Sc(26)), _mapToast, SR.Ctl.TitleMid);
                GUI.color = markColor;
            }
            } //end _mapVisible-only section
            GUI.DrawTexture(new Rect(e.mousePosition.x - SR.Ctl.Sc(7), e.mousePosition.y - SR.Ctl.Sc(7), SR.Ctl.Sc(15), SR.Ctl.Sc(15)), SR.Ctl.CursorTex);
            GUI.color = prevColor;
            if (prevF != null) GUI.skin.font = prevF;
        }

		private static ConfigEntry<bool> _gridAlwaysOn;

		public static bool GridAlwaysOn => _gridAlwaysOn != null && _gridAlwaysOn.Value;

		//开关随开随关：建造阶段总显示；行动阶段按“Grid Always On && 允许常驻”决定
		private static void GridToggleChanged(object s, EventArgs e)
		{
			try
			{
				if (_gridAlwaysOn == null) return;
				bool on;
				if (_gridPlacePhase) on = true;                       // 正在建造 → 保持网格
				else on = _gridAlwaysOn.Value && GridKeepAllowed();   // 行动/未知阶段
				foreach (Graphpaper gp in UnityEngine.Object.FindObjectsOfType<Graphpaper>())
				{
					if (gp == null) continue;
					if (on) gp.enableGrid(); else gp.disableGrid();
				}
			}
			catch (Exception __ex) { SR.Guard.Log("Experiments.GridKeepAllowed", __ex); }
		}

		//建造网格（Graphpaper）：原生靠 StartPhaseEvent 随阶段开关，但"派对盒规则"对局会整段跳过、
		//从不开关网格 → 本 mod 接管该事件统一决定：建造阶段任何模式都显示；行动阶段只在
		//“Grid Always On”开启且允许常驻（自由模式对局或“无视模式限制”）时保持显示。
		private static bool _gridPlacePhase;   // 最近一次阶段是否为建造(PLACE)

		//当前是否为自由模式**对局**（大厅/树屋不算，避免误开）
		private static bool GridFreePlayNow()
		{
			try {
				return LobbyManager.instance != null
					&& LobbyManager.instance.CurrentGameController is FreePlayControl;
			} catch { return false; }
		}

		//“行动阶段常驻”是否被允许：仅自由模式对局（“无视模式限制”开启 → 放开到任何模式）
		private static bool GridKeepAllowed()
		{
			if (SR.IgnoreModeLimit) return true;
			return GridFreePlayNow();
		}

		//接管阶段事件：按「阶段 + 模式 + 开关」决定网格显隐，覆盖原生（含派对盒）。
		[HarmonyPatch(typeof(Graphpaper), "handleEvent")]
		[HarmonyPrefix]
		private static bool GridAlwaysHandle(Graphpaper __instance, global::GameEvent.GameEvent e)
		{
			try
			{
				if (!SR.GateMaster) return true;
				StartPhaseEvent spe = e as StartPhaseEvent;
				if (spe == null) return true; // 非阶段事件 → 交给原方法（本就不处理）
				bool place = spe.Phase == GameControl.GamePhase.PLACE;
				bool play = spe.Phase == GameControl.GamePhase.PLAY;
				if (!place && !play) return true; // 其它阶段 → 交给原方法
				_gridPlacePhase = place;
				//建造阶段任何模式都显示；行动阶段仅当“Grid Always On && 允许常驻”时保持
				bool on = place
					|| (_gridAlwaysOn != null && _gridAlwaysOn.Value && GridKeepAllowed());
				if (on) __instance.enableGrid(); else __instance.disableGrid();
				return false; // 已接管，跳过原方法
			}
			catch { return true; }
		}

        //配置 Bind + 订阅 + 启动补一次都在这里（由 SR.Core 调用一次）。
        internal static void InitGrid(ConfigFile cfg) {
			_gridAlwaysOn = cfg.Bind<bool>("Freeplay", "Grid Always On", false, "地图网格：建造（放置方块）阶段任何模式都显示网格；本开关让**自由模式对局**在行动阶段也保持网格（游戏默认行动阶段不显示）。\n其它模式行动阶段不会因本开关点亮网格。\n随开随关：在自由对局里开启立即淡入，关闭立即淡出。开关在控制台“地图”栏目里。");
			_gridAlwaysOn.SettingChanged += GridToggleChanged;
			if (_gridAlwaysOn.Value)
			{
				GridToggleChanged(null, null); //上次会话开启过：立即补一次生效
			}
        }

        //按 section 渲染并入本页的配置条目
        private static void RenderSectionGroup(string sec) {
            if (SR.Ctl.InternalConfig == null) return;
            //列宽给足，避免并入的条目名在窄列折行（如“重生功能总开关”）
            float col = Mathf.Max(SR.Ctl.Sc(180), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(240));
            //用 SectionEntries：自动应用功能注册的行可见性（如 Camera 的快捷键已并入「自由相机」行）
            foreach (ConfigEntryBase e in SR.Ctl.SectionEntries(sec)) {
                SR.Ctl.RenderEntryRow(e, true, col);
            }
        }

        public static void Render() {
            //地图总开关（顶部，独立于本 Mod 总开关；关闭后 M 键无法打开地图）
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                Enabled ? SR.T("地图总开关：开", "Map Master: ON") : SR.T("地图总开关：关", "Map Master: OFF"),
                Enabled ? SR.Ctl.SelItem : SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(170)), GUILayout.Height(SR.Ctl.Sc(30)))) {
                SR.Ctl.SetValue(SR.Ctl.MapEnabledEntry, !Enabled);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.BeginHorizontal();
            ConfigEntryBase tm = SR.Ctl.FindEntry("Freeplay", "Treehouse Map");
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("树屋地图", "Treehouse map"), SR.T("允许在树屋大厅使用地图（M 键开）。地图主要用于自由模式；关闭后树屋不能开地图。", "Allow using the map in the treehouse (M key). The map is mainly for Freeplay; when off the map can't open in the treehouse.")), tm, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
            if (GUILayout.Button(TreehouseAllowed ? "✓" : "", TreehouseAllowed ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(30)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                if (tm != null) tm.BoxedValue = !TreehouseAllowed;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.BeginHorizontal();
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("地图按键", "Map key"), SR.T("打开/关闭地图窗口的按键（默认 M）", "Key to open/close the map (default M)")), SR.Ctl.MapKeyEntry, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
            GUILayout.Space(SR.Ctl.Sc(4));
            SR.Ctl.RenderControl(SR.Ctl.MapKeyEntry);
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.Label(SR.T("— 地图网格 —", "— Map grid —"), SR.Ctl.SecHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase ga = SR.Ctl.FindEntry("Freeplay", "Grid Always On");
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("地图网格", "Map grid"), SR.T("建造阶段任何模式都显示网格；本开关让自由模式对局在行动阶段也保持网格（游戏默认行动阶段不显示）。", "The build grid shows while building in every mode; this switch keeps it on during the play phase in freeplay matches only (vanilla hides it during play).")), ga, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
            if (ga != null) SR.Ctl.RenderControl(ga);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));

            //视野（section 名为 "Camera"）
            GUILayout.Label(SR.T("— 视野 —", "— Camera —"), SR.Ctl.SecHeader);
            RenderSectionGroup("Camera");
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.Label(SR.T("— 重生 —", "— Respawn —"), SR.Ctl.SecHeader);
            RenderSectionGroup("Respawn");
            GUILayout.Space(SR.Ctl.Sc(4));
        }

        private static void SelfRegFreeplay() {
            SR.Nav("Freeplay", 70); //侧栏栏目（顺序 70）
            SR.LocDesc("Freeplay", "Grid Always On", "地图网格：建造（放置方块）阶段任何模式都显示网格；本开关让自由模式对局在行动阶段也保持网格（游戏默认行动阶段不显示）。\n其它模式行动阶段不会因本开关点亮网格。\n随开随关：在自由对局里开启立即淡入，关闭立即淡出。\n（开关在地图栏目里）", "Map grid: the build grid shows while building (placing) in every mode. This switch additionally keeps it visible during the play/action phase in freeplay matches only (vanilla hides it during action).\nOther modes never light it up during action because of this switch.\nOn/off takes effect instantly inside a freeplay match.\n(Switch lives in the Map tab)");
        }

	}
}
