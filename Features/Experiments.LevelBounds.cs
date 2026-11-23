// 实验 · 关卡边界页：页面渲染 + 边界解除本体 + 解除 UI3 覆盖。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    // 关卡边界（实验）：解除制作关卡时的空间边界限制，与满度上限无关。
    // 四条路：实体墙 size 归零 / 光标边界换 Huge / 相机取景边界 / 方块 IgnoreBounds；均记录原值可还原。
    [SrFeature("sr.levelboundsunlock", "LevelBoundsUnlock", "1.0")]
    public class LevelBoundsUnlock : ITweak {
        private static ConfigEntry<bool> _on;   // 主开关
        private static ConfigEntry<int> _radius;

        internal static string Status = "（尚未探测：打开上面的开关后约 1 秒出现结果）";
        // 方块越界放置并入主开关：IgnoreBounds 是纯 bool，CanPlaceCheck 直接读。
        // 新方块由 PiecePlacedEvent 即时打标，全场扫描只在开启 / 换场景后做有限次；关闭时一律走 Restore 还原。
        private static bool WallsOn { get { return _on != null && _on.Value; } }
        private static bool CursorOn { get { return _on != null && _on.Value; } }
        private static bool CameraOn { get { return _on != null && _on.Value; } }

        internal static bool PieceIgnoreOn {
            get { return _on != null && _on.Value; }
        }

        // 供 RemoveUI3 判断主开关；判据含 SRApi.GateMaster，与 Reeval/Tick 一致。
        internal static bool MasterOn { get { return _on != null && _on.Value && SRApi.GateMaster; } }

        private static readonly Dictionary<Collider2D, Vector2> _colOrig = new Dictionary<Collider2D, Vector2>();
        private static readonly Dictionary<MonoBehaviour, bool> _fixWas = new Dictionary<MonoBehaviour, bool>();
        // fixWas 兼作"被动过的相机控制器"清单：关闭时不按原值还原 enabled，而是清 isCameraConfigured 并重新启用。
        // 光标边界原值只记 boundary，不用 SetBounds(Collider2D) 重载。
        private static readonly Dictionary<PiecePlacementCursor, Bounds> _curOrig = new Dictionary<PiecePlacementCursor, Bounds>();
        private static readonly Dictionary<Placeable, bool> _igWas = new Dictionary<Placeable, bool>();
        private static readonly HashSet<Placeable> _aliveBuf = new HashSet<Placeable>();
        private static int _igScans;                       // 本场景全场扫描次数（达到上限后只靠事件打标）
        private const int IgScanMaxTries = 3;               // 开启/换场景后只补扫 3 次（≈1.5 秒）；新方块由放置事件处理
        private static readonly List<Placeable> _deadBuf = new List<Placeable>();
        private static System.Reflection.FieldInfo _fiBoundary;
        private static System.Reflection.FieldInfo _fiFrame;
        private static bool _fiTried;
        private static string _hardNote;
        private static int _hardNoteR = int.MinValue;
        private static int _hardNoteN = -1;
        private static string _hardNoteCam;
        private static Bounds _zcOrig = new Bounds(Vector3.zero, Vector3.zero);
        private static bool _zcHas;
        private static bool _zcWasEnabled;
        // 相机是否被改过：未改过时 Restore 不得动 zc.enabled。
        private static bool _zcTouched;
        private static int _tick;
        private static bool _camDiagDone;
        private static bool _applied;

        private static readonly global::Level[] _noLevels = new global::Level[0];
        private static global::Level[] _lvCache = null;
        private static int _lvCacheAt = -9999;
        // 墙扫描时钟必须独立于 Levels 的时钟，共用会让 Level 扫描缓存永久失效。
        private static int _wallScanAt = -9999;
        private static readonly PiecePlacementCursor[] _noCursors = new PiecePlacementCursor[0];
        private static PiecePlacementCursor[] _cursors = _noCursors;
        private static int _cursorsAt = -9999;
        private static ZoomCamera _zc;
        private static int _zcAt = -9999;
        private static FixCameraToBoundaries[] _fixers = new FixCameraToBoundaries[0];
        private static int _fixersAt = -9999;
        private static readonly BoxCollider2D[] _wallCol = new BoxCollider2D[3];

        private static readonly string[] SolidWallNames = { "TopBoundary", "LeftBoundary", "RightBoundary" };
        // size 置零即解除（靠 transform.scale 撑开，放大无效且精灵变形）；判据为 BLANKLEVEL + 四边齐全。
        private static void SelfReg() {
            // 两处反射均需登记，否则改名后静默返回 null（表现为解除无效）。
            SRApi.Ref.Override("Cursor.boundary", "解除关卡边界：放置光标边界快照");
            SRApi.Ref.Override("ZoomCamera.frame", "解除关卡边界：相机取景框");
            TweakCfg.Entry("Experiments", "Level Bounds", "解除关卡边界", null,
                "解锁关卡边界\n同时解除实体墙、光标、相机、界外黑幕与越界放置\n只影响本地，不改存档与联机状态",
                "Unlock level bounds\nremoves walls, cursor, camera and out-of-bounds limits at once\nlocal only; save and network state untouched");
            TweakCfg.Entry("Experiments", "Level Bounds Radius", "硬解除半径", "Hard radius",
                "硬解锁半径\n光标与相机能到关卡外多远\n不限上限；1＝贴着光标；仅主开关开启时生效",
                "Hard unlock radius\nhow far the cursor and camera reach outside the level\nno upper limit; 1 = at the cursor; main switch only");
        }

        private class PieceIgnoreListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    GameEvent.PiecePlacedEvent ppe = e as GameEvent.PiecePlacedEvent;
                    if (ppe == null || ppe.PlacedBlock == null) return;
                    if (_on == null || !_on.Value || !SRApi.GateMaster) return;
                    Placeable p = ppe.PlacedBlock;
                    if (!_igWas.ContainsKey(p)) _igWas[p] = p.IgnoreBounds;
                    if (!p.IgnoreBounds) p.IgnoreBounds = true;
                } catch (Exception __ex) { SRApi.Guard.Log("边界/新方块打标", __ex); }
            }
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _on = plugin.Config.Bind("Experiments", "Level Bounds", false,
                    "解除关卡边界：实体墙 + 相机取景 + 光标范围 + 方块越界（与满度上限无关）。");
                _radius = plugin.Config.Bind("Experiments", "Level Bounds Radius", DefaultHardRadiusInt,
                    "硬解除能走多远. 不限范围, 填 1 就是贴着光标走.");
                // 一次性迁移仍为旧默认值的配置；标记放 [Internal] 段，不渲染也不被当孤儿键清掉。
                ConfigEntry<bool> lbDefaults = plugin.Config.Bind("Internal", "Level Bounds Defaults v3", false,
                    "内部：硬解除半径的默认值已迁移（200）");
                if (!lbDefaults.Value) {
                    try {
                        if (_radius.Value == 100000 || _radius.Value == 320 || _radius.Value == 10) _radius.Value = DefaultHardRadiusInt;
                        lbDefaults.Value = true;
                    } catch (Exception __ex) { SRApi.Guard.Log("边界默认值迁移", __ex); }
                }

                // 事件驱动即时重评估，Tick 仅作兜底（面板关闭时 Tick 可能不跑）。
                if (_on != null) {
                    System.EventHandler __h_boundsOn = (s, e) => Reeval();
                    _on.SettingChanged += __h_boundsOn;
                    Sub.Revoke(() => _on.SettingChanged -= __h_boundsOn);
                }
                // 半径变更只重跑 ApplyHardBounds（幂等），不触发 Reeval 的全流程重扫。
                if (_radius != null) {
                    System.EventHandler __h_boundsRadius = (s, e) => { if (_on != null && _on.Value) { try { ApplyHardBounds(); } catch (Exception __ex) { SRApi.Guard.Log("边界半径刷新", __ex); } } };
                    _radius.SettingChanged += __h_boundsRadius;
                    Sub.Revoke(() => _radius.SettingChanged -= __h_boundsRadius);
                }
                // 半径不挂 SettingChanged，Tick 每帧会调 ApplyHardBounds，改完下一帧自然生效。
                // 旧的子项键已无任何 Config.Bind，统一由 SR.ConfigMigration 的 RemoveKeys 清理。
                SRApi.RegisterTick("关卡边界", Tick);
            SRApi.RegisterSceneHook(delegate {
                _cursors = null; _lvCache = null; _zc = null; _fixers = null;
                _igScans = 0;
            });   // 换场景才重扫：边界钩子不再按帧做全场景 FindObjectsOfType
                try { GameEvent.GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(new PieceIgnoreListener(), true); }
                catch (Exception __ex) { SRApi.Guard.Log("边界/事件监听", __ex); }
                SRApi.RegisterSceneHook(Reset);
                SRApi.RegisterDismissHook(Dismiss);
                // 总开关关闭时核心触发 RegisterDismissHook -> Dismiss() -> Restore()。
            } catch (Exception __ex) { SRApi.Guard.Log("关卡边界 初始化", __ex); }
        }

        // 事件驱动重评估：开则应用、关则还原；幂等，不依赖 Tick。
        private static void Reeval() {
            try {
                if (_on == null) return;
                _igScans = 0;   // 重新开启：允许再做一轮有限次全场扫描
                bool on = _on.Value && SRApi.GateMaster;
                if (on) {
                    _applied = true;
                    // 刚打开就立刻解一次 UI3，不等边界重放的节拍。
                    if (RemoveUI3.WantOn) {
                        try { RemoveUI3.Tick(true); } catch (Exception __ui0) { SRApi.Guard.Log("解除UI3/即时", __ui0); }
                    }
                    ResetScanCounters();
                    BeginScan();          // 打开就全速扫，扫到即停 / 超时自停
                    if (WallsOn) EnsureWalls(true);
                    OpenSolidWalls(WallsOn);
                    ApplyHardBounds();
                    SuppressFixCamera(CameraOn);
                    ApplyAllPiecesIgnore();
                    BuildStatus();
                } else {
                    ApplyCursorPiece(false);
                    if (_applied) { Restore(); _applied = false; }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("关卡边界 重评估", __ex); }
        }

        // 扫描策略：打开开关 / 换场景后全速逐帧扫描；扫到（墙齐或确认本关没有）即停；
        // 未扫到则超时自动停止，之后只维持已生效的解绑。
        private const float ScanTimeout = 3f;
        private static float _scanDeadline = -1f;
        private static bool _scanRunning;
        private static int _scanScene = -9999;

        private static void BeginScan() {
            _scanRunning = true;
            _scanDeadline = Time.unscaledTime + ScanTimeout;
        }

        internal static void Tick() {
            try {
                if (_on == null) return;
                bool on = _on.Value && SRApi.GateMaster;
                if (!on) {
                    if (_scanRunning) { _scanRunning = false; }
                    ApplyCursorPiece(false);
                    if (_applied) { Restore(); _applied = false; }
                    return;
                }
                int __scene = -9999;
                try { __scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { /* 静默：取不到场景就沿用上一次 */ }
                if (__scene != _scanScene) {
                    _scanScene = __scene;
                    for (int i = 0; i < _wallCol.Length; i++) _wallCol[i] = null;
                    ResetScanCounters();
                    BeginScan();
                }
                if (_scanDeadline < 0f) BeginScan();   // 刚打开开关
                bool scanning = _scanRunning && Time.unscaledTime <= _scanDeadline;
                if (!scanning && _scanRunning) { _scanRunning = false; }

                _tick++;
                _applied = true;
                if (WallsOn) {
                    EnsureWalls(scanning);
                    if (scanning && WallsResolved()) { _scanRunning = false; }
                }
                if (RemoveUI3.WantOn) {
                    try { RemoveUI3.Tick(scanning); } catch (Exception __ui) { SRApi.Guard.Log("解除UI3/并入", __ui); }
                }
                // 解绑是幂等写值：扫描阶段每帧一次，之后 4Hz 维持。
                if (scanning || _tick % 60 == 0) {
                    ApplyCursorPiece(true);
                    OpenSolidWalls(WallsOn);
                    ApplyHardBounds();
                    SuppressFixCamera(CameraOn);
                }
                if (!scanning && (_tick % 120 == 0)) {
                    ApplyAllPiecesIgnore();
                    BuildStatus();
                }
            } catch (Exception __ex) { SRApi.Guard.Log("关卡边界 Tick", __ex); }
        }

        // 三面实体墙：只在引用缺失时扫场景（fullSpeed = 扫描阶段不节流，否则低频维持）。
        private static int _wallScanTries;
        private static int _wallScanScene = -1;
        private const int WallScanMaxTries = 240;   // 全速时约 4 秒；仍找不到就认为本关没有这三面墙

        private static void ResetScanCounters() {
            _wallScanTries = 0;
            _wallScanScene = -1;
            _wallScanAt = -9999;
        }

        private static bool WallsResolved() {
            for (int i = 0; i < _wallCol.Length; i++) if (_wallCol[i] == null) return _wallScanTries >= WallScanMaxTries;
            return true;
        }

        private static void EnsureWalls(bool fullSpeed = false) {
            bool miss = false;
            for (int i = 0; i < 3; i++) if (_wallCol[i] == null) { miss = true; break; }
            if (!miss) return;
            int sceneHandle = -1;
            try { sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { /* 静默：取不到场景沿用上次 */ }
            if (sceneHandle != _wallScanScene) { _wallScanScene = sceneHandle; _wallScanTries = 0; }
            if (_wallScanTries >= WallScanMaxTries) return;   //本场景已放弃：它没有这三面墙
            if (!fullSpeed && Time.frameCount - _wallScanAt < 60) return;   //维持态才节流（1s 一次即可）
            _wallScanAt = Time.frameCount;
            _wallScanTries++;
            try {
                // 墙名是已知常量：直接按名查找，避免 FindObjectsOfType 的全场景分配与遍历。
                for (int k = 0; k < SolidWallNames.Length; k++) {
                    if (_wallCol[k] != null) continue;
                    GameObject g = GameObject.Find(SolidWallNames[k]);
                    if (g == null) continue;
                    BoxCollider2D bc = g.GetComponent<BoxCollider2D>();
                    if (bc != null) _wallCol[k] = bc;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        private static void OpenSolidWalls(bool want) {
            try {
                // 改 size 后必须 SyncPhysics()：宽相不重建会让 InBounds 被打回 false 而放不下。
                bool changed = false;
                for (int i = 0; i < 3; i++) {
                    BoxCollider2D bc = _wallCol[i];
                    if (bc == null) continue;
                    Vector2 orig;
                    if (want) {
                        if (!_colOrig.TryGetValue(bc, out orig)) { orig = bc.size; _colOrig[bc] = orig; }
                        if (bc.size != Vector2.zero) {
                            bc.size = Vector2.zero;
                            bool was = bc.enabled;
                            if (was) { bc.enabled = false; bc.enabled = true; }
                            changed = true;
                        }
                    } else if (_colOrig.TryGetValue(bc, out orig)) {
                        if (bc.size != orig) {
                            bc.size = orig;
                            bool was = bc.enabled;
                            if (was) { bc.enabled = false; bc.enabled = true; }
                            changed = true;
                        }
                        _colOrig.Remove(bc);
                    }
                }
                if (changed) SyncPhysics();   // 与其它改 collider 几何的路径一致
            } catch (Exception __ex) { SRApi.Guard.Log("解除实体墙", __ex); }
        }

        // 相机取景集合不干预：界外的黑来自无背景时的相机清除色。
        // CamNote() 为只读诊断，只输出坐标不改相机状态。

        // 挂在「相机」子开关上（默认关）：关闭时完全不碰相机，开启时禁用游戏组件并保证 ZoomCamera 开启。
        private static void SuppressFixCamera(bool want) {
            try {
                FixCameraToBoundaries[] fs = Fixers();
                if (!want) {
                    // 关闭时不能只还原 enabled：FixCameraToBoundaries 配置完会置 isCameraConfigured 并自关，须清该字段并重新启用。
                    if (_fixWas.Count == 0 && fs.Length == 0) return;
                    int nb = 0;
                    // ① 曾记录的控制器（可能已不在当前扫描结果里）
                    foreach (KeyValuePair<MonoBehaviour, bool> kv in _fixWas) {
                        FixCameraToBoundaries f = kv.Key as FixCameraToBoundaries;
                        if (f == null) continue;
                        try { f.isCameraConfigured = false; if (!f.enabled) f.enabled = true; nb++; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    }
                    _fixWas.Clear();
                    // ② 当前场景中的控制器
                    for (int i = 0; i < fs.Length; i++) {
                        FixCameraToBoundaries f = fs[i];
                        if (f == null) continue;
                        try { f.isCameraConfigured = false; if (!f.enabled) f.enabled = true; nb++; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    }
                    // ③ 曾打开的 ZoomCamera 按原值还回
                    if (_zcTouched) {
                        try { ZoomCamera z0 = Zc(); if (z0 != null && z0.enabled != _zcWasEnabled) z0.enabled = _zcWasEnabled; }
                        catch (Exception __ze) { SRApi.Guard.Log("还原 ZoomCamera.enabled", __ze); }
                        _zcTouched = false;
                    }
                    if (nb > 0) SRApi.LogInfo("[关卡边界] 相机项关闭，已把相机交回游戏（" + nb + " 个相机控制器重新接管）");
                    return;
                }
                for (int i = 0; i < fs.Length; i++) {
                    FixCameraToBoundaries f = fs[i];
                    if (f == null) continue;
                    if (!_fixWas.ContainsKey(f)) _fixWas[f] = f.enabled;
                    if (f.enabled) f.enabled = false;
                }
                // FixCameraToBoundaries 会关闭 ZoomCamera 并自行接管；禁用该组件后必须重开 ZoomCamera，否则相机无人接管。
                ZoomCamera zc = Zc();
                if (zc != null) {
                    if (!_zcTouched) { _zcWasEnabled = zc.enabled; _zcTouched = true; }   // 只在首次记录原值
                    if (!zc.enabled) zc.enabled = true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("压制 FixCamera", __ex); }
        }

        private static void ApplyCursorPiece(bool on) {
            try {
                bool want = on && PieceIgnoreOn;
                PiecePlacementCursor[] curs = Cursors();
                for (int i = 0; i < curs.Length; i++) {
                    PiecePlacementCursor c = curs[i];
                    if (c == null) continue;
                    Placeable p = c.Piece;
                    if (p == null) continue;
                    if (p.IgnoreBounds != want) p.IgnoreBounds = want;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("关卡边界-手上方块", __ex); }
        }

        private static void ApplyAllPiecesIgnore() {
            try {
                bool on = PieceIgnoreOn;
                Placeable[] all = UnityEngine.Object.FindObjectsOfType<Placeable>();
                // 本场景最多扫 IgScanMaxTries 次；此后新方块由放置事件处理。
                if (_igScans >= IgScanMaxTries) return;
                _igScans++;
                // _igWas 是强引用字典且销毁方块不派发事件，故以本轮扫描集合为权威剔除；复用静态缓冲避免 GC 尖峰。
                HashSet<Placeable> alive = null;
                if (_igWas.Count > 0) { _aliveBuf.Clear(); alive = _aliveBuf; }
                for (int i = 0; i < all.Length; i++) {
                    Placeable p = all[i];
                    if (p == null) continue;
                    if (alive != null) alive.Add(p);
                    if (!_igWas.ContainsKey(p)) _igWas[p] = p.IgnoreBounds;
                    if (p.IgnoreBounds != on) p.IgnoreBounds = on;
                }
                if (alive != null) {
                    _deadBuf.Clear();
                    List<Placeable> dead = _deadBuf;
                    foreach (Placeable k in _igWas.Keys) {
                        if (alive.Contains(k)) continue;
                            dead.Add(k);
                    }
                    if (dead != null) for (int i = 0; i < dead.Count; i++) _igWas.Remove(dead[i]);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("全场方块 IgnoreBounds", __ex); }
        }


        // 改运行中 Collider2D 的 size 后同步物理宽相；不改 enabled：那是 Level.EnablePlacementBounds 的职责。
        private static void SyncPhysics() {
            try { Physics2D.SyncTransforms(); }
            catch (Exception __ex) { SRApi.Guard.Log("同步物理宽相", __ex); }
        }

        // 硬解除半径默认值，取值不限范围；半径必须 > 0，零尺寸 bounds 会让 ZoomCamera 取景框塌成一点。
        private const int DefaultHardRadiusInt = 200;
        private const float DefaultHardRadius = 200f;   // 兜底半径，与默认值一致
        private static float HardRadius() {
            try {
                if (_radius != null) {
                    float r = (float)_radius.Value;
                    if (r > 0f) return r;
                    SRApi.LogWarn("[关卡边界] 硬解除半径必须大于 0, 已按 1 处理（填 0 会让取景框塌成一个点）");
                    return 1f;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return DefaultHardRadius;
        }
        private static Bounds Huge(Vector3 center) {
            float r = HardRadius();
            return new Bounds(center, new Vector3(r * 2f, r * 2f, 0f));
        }
        private static System.Reflection.FieldInfo BoundaryField() {
            if (_fiTried) return _fiBoundary;
            _fiTried = true;
            try {
                for (Type t = typeof(PiecePlacementCursor); t != null; t = t.BaseType) {
                    System.Reflection.FieldInfo fi = RefCache.FieldOf(t, "boundary");
                    if (fi != null) { _fiBoundary = fi; break; }
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return _fiBoundary;
        }
        // ZoomCamera.frame 是 protected 字段，即相机实际取景框。
        private static System.Reflection.FieldInfo FrameField() {
            if (_fiFrame != null) return _fiFrame;
            try {
                _fiFrame = RefCache.FieldOf(typeof(ZoomCamera), "frame");
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return _fiFrame;
        }
        private static bool ReadCursorBounds(PiecePlacementCursor c, out Bounds b) {
            b = default(Bounds);
            try {
                System.Reflection.FieldInfo fi = BoundaryField();
                if (fi == null) return false;
                object v = fi.GetValue(c);
                if (v == null) return false;
                b = (Bounds)v;
                return true;
            } catch { return false; }
        }

        // 写回已记录的光标原值（光标子开关关闭与功能关闭共用，避免原值残留）。
        private static void RestoreCursorBounds(string why) {
            if (_curOrig.Count == 0) return;
            int nb = 0;
            foreach (KeyValuePair<PiecePlacementCursor, Bounds> kv in _curOrig) {
                if (kv.Key == null) continue;
                try { kv.Key.SetBounds(kv.Value); nb++; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            }
            _curOrig.Clear();
            if (nb > 0) SRApi.LogInfo("[关卡边界] " + why + "，已还原 " + nb + " 个光标");
        }

        // 上次写入的硬解除半径：没变就不重复 SetBounds。
        private static float _hardRadiusApplied = float.NaN;

        private static void ApplyHardBounds() {
            try {
                if (_on == null || !_on.Value) return;
                float r = HardRadius();
                // 半径没变且本场景已写过就跳过，只挡纯重复写。
                int n = 0;
                bool radiusChanged = float.IsNaN(_hardRadiusApplied) || Mathf.Abs(_hardRadiusApplied - r) >= 0.0001f;
                if (CursorOn) {
                    PiecePlacementCursor[] curs = Cursors();
                    for (int i = 0; i < curs.Length; i++) {
                        PiecePlacementCursor c = curs[i];
                        if (c == null) continue;
                        Bounds cb;
                        if (ReadCursorBounds(c, out cb) && !_curOrig.ContainsKey(c)) _curOrig[c] = cb;
                        // 只用 SetBounds(Bounds) 写 boundary；SetBounds(Collider2D) 会改写 boundingCollider，改错后再也放不下。
                        Bounds want = Huge(c.transform.position);
                        if (!radiusChanged && cb.size.x > 1e6f) continue;
                        try { c.SetBounds(want); n++; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    }
                } else {
                    // 子开关关闭：立刻写回原值。
                    RestoreCursorBounds("光标越界关闭");
                }
                string cam;
                {
                    // 相机取景写 Huge(LevelCenter())：派对盒阶段 targets/boxes 为空，取景框会被拉爆，故整段挂在「相机」子开关上。
                    if (CameraOn) {
                        ZoomCamera zc = Zc();
                        if (zc != null) {
                            if (!_zcHas) { _zcOrig = zc.GetBounds(); _zcHas = true; }
                            zc.SetBounds(Huge(LevelCenter()));   // 半径取自 HardRadius()
                            cam = "相机✓";
                        } else cam = "无相机";
                    } else {
                        if (_zcHas) {
                            try { ZoomCamera z0 = Zc(); if (z0 != null) z0.SetBounds(_zcOrig); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                            _zcHas = false;
                        }
                        cam = "相机=不动";
                    }
                }
                // 每帧只比较三个值，脏标记只在变化时重建文案。
            if ((int)r != _hardNoteR || n != _hardNoteN || !string.Equals(cam, _hardNoteCam, StringComparison.Ordinal)) {
                _hardNoteR = (int)r; _hardNoteN = n; _hardNoteCam = cam;
                _hardNote = "硬解除 r=" + (int)r + " 光标×" + n + " " + cam;
            }
            } catch (Exception __ex) { _hardNote = "硬解除失败:" + __ex.Message; SRApi.Guard.Log("关卡边界-硬解除", __ex); }
        }

        private const int CursorRefreshEvery = 600;   // 10 秒兜底：正常只在换场景时重扫
        private static PiecePlacementCursor[] Cursors() {
            int f = Time.frameCount;
            // 换场景由 SceneHook 清空缓存；场景内只有新玩家加入会新增光标，10 秒兜底即可。
            if (_cursors != null && _cursors.Length > 0 && f - _cursorsAt < CursorRefreshEvery) return _cursors;
            _cursorsAt = f;
            try {
                PiecePlacementCursor[] a = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (a != null) _cursors = a;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return _cursors;
        }
        private static global::Level[] Levels() {
            int f = Time.frameCount;
            if (_lvCache != null && _lvCache.Length > 0) return _lvCache;   // 换场景才失效
            _lvCacheAt = f;
            try {
                global::Level[] a = UnityEngine.Object.FindObjectsOfType<global::Level>();
                if (a != null) _lvCache = a;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return _lvCache != null ? _lvCache : _noLevels;
        }
        private static global::Level FirstLevel(global::Level[] lvs) {
            if (lvs == null) return null;
            for (int i = 0; i < lvs.Length; i++) if (lvs[i] != null) return lvs[i];
            return null;
        }
        private static ZoomCamera Zc() {
            int f = Time.frameCount;
            if (f - _zcAt < 600 && _zc != null) return _zc;   // 10 秒兜底；换场景会重置
            _zcAt = f;
            try {
                ZoomCamera a = null; ZoomCamera[] zs = SRApi.FrameObjects<ZoomCamera>(); if (zs != null) for (int zi = 0; zi < zs.Length; zi++) { if (zs[zi] != null) { a = zs[zi]; break; } }
                if (a != null) _zc = a;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return _zc;
        }
        private static FixCameraToBoundaries[] Fixers() {
            int f = Time.frameCount;
            if (_fixers != null && _fixers.Length > 0) return _fixers;      // 换场景才失效
            _fixersAt = f;
            try {
                FixCameraToBoundaries[] a = UnityEngine.Object.FindObjectsOfType<FixCameraToBoundaries>();
                if (a != null) _fixers = a;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return _fixers;
        }
        private static Vector3 LevelCenter() {
            global::Level lv = FirstLevel(Levels());
            if (lv == null) return Vector3.zero;
            try { if (lv.CameraBounds != null) return lv.CameraBounds.bounds.center; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            try { return lv.GetCameraBounds().center; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return Vector3.zero;
        }

        private static void BuildStatus() {
            try {
                List<string> bits = new List<string>();
                List<string> walls = new List<string>();
                for (int i = 0; i < 3; i++) {
                    BoxCollider2D bc = _wallCol[i];
                    string st = bc == null ? "未找到" : (bc.size == Vector2.zero ? "已解除" : ("size=" + ((int)bc.size.x) + "x" + ((int)bc.size.y)));
                    walls.Add(SolidWallNames[i] + "=" + st);
                }
                bits.Add("实体墙 " + string.Join(" ", walls.ToArray()));
                global::Level lv = FirstLevel(Levels());
                if (lv != null) {
                    if (lv.thisLevelis == GameState.LevelName.BLANKLEVEL
                        && lv.Left != null && lv.Right != null && lv.Top != null && lv.Bottom != null) {
                        float w = (lv.Right.position.x - lv.Left.position.x) + 10f;
                        float h = (lv.Top.position.y - lv.Bottom.position.y) + 10f;
                        bits.Add("四边围出 " + ((int)w) + "x" + ((int)h));
                    } else bits.Add("非自定义关卡（四边未动）");
                } else bits.Add("无 Level 对象");
                bits.Add(CamNote());
                Status = (_hardNote != null ? _hardNote + " | " : "")
                       + string.Join("  ", bits.ToArray());
            } catch (Exception __ex) { Status = "状态生成失败：" + __ex.Message; SRApi.Guard.Log("关卡边界 状态", __ex); }
        }

        private static string CamNote() {
            try {
                ZoomCamera zc = Zc();
                if (zc == null) return "相机=无";
                Bounds fr = default(Bounds);
                System.Reflection.FieldInfo fi = FrameField();
                if (fi != null) { try { object v = fi.GetValue(zc); if (v != null) fr = (Bounds)v; } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
                Bounds bd = zc.GetBounds();
                Vector3 cp = zc.transform.position;
                string curs = "光标=无";
                PiecePlacementCursor[] curs2 = Cursors();
                for (int i = 0; i < curs2.Length; i++) {
                    if (curs2[i] == null) continue;
                    Vector3 p = curs2[i].transform.position;
                    curs = "光标(" + ((int)p.x) + "," + ((int)p.y) + ")";
                    break;
                }
                string s = "取景框 " + ((int)fr.size.x) + "x" + ((int)fr.size.y)
                         + " 相机(" + ((int)cp.x) + "," + ((int)cp.y) + ")"
                         + " 边界 " + ((int)bd.size.x) + "x" + ((int)bd.size.y)
                         + " " + curs;
                if (!_camDiagDone && (fr.size.x > 400f || fr.size.y > 400f)) {
                    _camDiagDone = true;
                    SRApi.LogWarn("[关卡边界] 取景框被拉得很大（" + ((int)fr.size.x) + "x" + ((int)fr.size.y)
                        + "） -  取景框被撑到这么大时，画面里所有东西都会小到看不见。");
                }
                return s;
            } catch (Exception __ex) { SRApi.Guard.Log("相机诊断", __ex); return "相机诊断失败"; }
        }

        internal static void Restore() {
            List<string> rb = new List<string>();
            try {
                int nc = 0;
                foreach (KeyValuePair<Collider2D, Vector2> kv in _colOrig) {
                    if (kv.Key == null) continue;
                    BoxCollider2D bc = kv.Key as BoxCollider2D;
                    if (bc == null) continue;
                    bc.size = kv.Value;
                    nc++;   // 只写 size，不碰 enabled
                }
                // 同步物理宽相，让 trigger 关系立即重建，否则放置校验会判"放不下"。
                SyncPhysics();
                _colOrig.Clear();
                rb.Add("碰撞体×" + nc);
                int nf = 0;
                foreach (KeyValuePair<MonoBehaviour, bool> kv in _fixWas) { if (kv.Key == null) continue; kv.Key.enabled = kv.Value; nf++; }
                _fixWas.Clear();
                rb.Add("FixCamera×" + nf);
                int nig = 0;
                foreach (KeyValuePair<Placeable, bool> kv in _igWas) { if (kv.Key == null) continue; kv.Key.IgnoreBounds = kv.Value; nig++; }
                _igWas.Clear();
                rb.Add("方块IgnoreBounds×" + nig);
                int ncur = 0;
                foreach (KeyValuePair<PiecePlacementCursor, Bounds> kv in _curOrig) {
                    if (kv.Key == null) continue;
                    try {
                        // 一律 SetBounds(Bounds) 只还原 boundary；SetBounds(Collider2D) 会改写 boundingCollider 且无复位途径。
                        kv.Key.SetBounds(kv.Value);
                        ncur++;
                    } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
                _curOrig.Clear();
                rb.Add("光标原值×" + ncur);
                ZoomCamera zc = Zc();
                if (zc != null && _zcHas) { try { zc.SetBounds(_zcOrig); rb.Add("相机=原快照"); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
                _zcHas = false;
                // 仅相机项开过且原值本为关闭时把 ZoomCamera.enabled 关回，其余情况不动。
                if (_zcTouched) { try { ZoomCamera zr = Zc(); if (zr != null && !_zcWasEnabled && zr.enabled) zr.enabled = false; } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
                _zcTouched = false;
                Status = "（已还原为游戏原值）" + string.Join(" ", rb.ToArray());
            } catch (Exception __ex) { Status = "（还原异常：" + __ex.Message + "）已还原：" + string.Join(" ", rb.ToArray()); SRApi.Guard.Log("关卡边界 还原", __ex); }
            _hardNote = null;
            _camDiagDone = false;
        }

        // 换场景：Level / 相机 / 光标 / 方块都是新对象，旧原值字典整体作废。
        private static void Reset() {
            _igScans = 0;
            // 不要把 *At 时间戳清成 -9999：Reset 会被频繁触发，清掉等于让 Cursors()/Levels() 缓存失效，
            // 退回每帧 FindObjectsOfType；场景换了对象自然为 null，调用方已判空。
            _igScans = 0;

            _colOrig.Clear();
            _fixWas.Clear();
            _curOrig.Clear();
            _igWas.Clear();
            for (int i = 0; i < 3; i++) { _wallCol[i] = null; }
            _zc = null; _zcAt = -9999;
            _zcHas = false;
            _zcTouched = false;
            _cursors = _noCursors; _cursorsAt = -9999;
            _lvCache = null; _lvCacheAt = -9999;
            _wallScanAt = -9999;   // 两个时钟都要归零
            _wallScanTries = 0; _wallScanScene = -1;   // 扫描次数上限也清零
            _fixers = new FixCameraToBoundaries[0]; _fixersAt = -9999;
            _hardNote = null;
            _camDiagDone = false;
            _applied = false;
            Status = "（换场景：已重置，等待下次应用）";
            // 不要清 _tick / _nextApplyAt：Reset 频繁触发，清掉会让 2Hz 门限失效而每帧跑重活。
        }

        private static void Dismiss() {
            try { if (_applied) { Restore(); _applied = false; } }
            catch (Exception __ex) { SRApi.Guard.Log("关卡边界 收起", __ex); }
        }
    }
}
