// 实验 · 其它页：自由相机 + 加载后清理（GC）。

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
    {
        // 其它页：自由相机 / 加载后清理
        public static void RenderPage2() {
            try {
                // 自由相机
                SRApi.Ctl.SectionTitle("- " + SRApi.T("自由相机", "Free camera") + " -", SRApi.T("开启后可以用滚轮自由缩放视野. 关闭后完全恢复游戏默认相机. 只在自由模式有效.",
                     "While on, the wheel zooms the view freely. Off = fully restores the default camera. Freeplay only."));
                SRApi.RenderSectionEntries("Experiments", delegate (ConfigEntryBase e) {
                    // 相机移动速度归入自由相机分区（只对自由相机生效）。
                    return e.Definition.Key == "Free Camera" || e.Definition.Key == "Free Camera FOV"
                        || e.Definition.Key == "Free Camera Speed";
                });
                if (FreeCam.SpeedNote != null && FreeCam.SpeedNote.Length > 0) {
                    GUILayout.Space(SRApi.Ctl.Sc(2));
                    DrawStatus(FreeCam.SpeedNote);
                }

                // 跨平台联机：直接改游戏自己的 CrossPlatformToggle（反射定位，不绑版本）。
                GUILayout.Space(SRApi.Ctl.Sc(6));
                SRApi.Ctl.SectionTitle("- " + SRApi.T("跨平台联机", "Cross-play") + " -",
                    SRApi.T("开：匹配池包含其它平台（走 Unity 大厅/中继，延迟通常更高）；关：只和同平台玩家联机. "
                        + "新建存档的默认值是关.",
                        "ON: the matchmaking pool includes other platforms (Unity lobby + relay, usually higher ping); "
                        + "OFF: same platform only. New saves default to OFF."));
                GUILayout.BeginHorizontal();
                if (!CrossPlay.Available) {
                    GUILayout.Label(SRApi.T("当前游戏版本找不到该设置（开关不可用）", "Setting not found in this game build"), SRApi.Ctl.Label);
                } else {
                    bool on = CrossPlay.Read();
                    GUILayout.Label(SRApi.T("当前：", "Now: ") + (on ? SRApi.T("开", "ON") : SRApi.T("关", "OFF")),
                        SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(96)));
                    if (GUILayout.Button(on ? SRApi.T("关闭", "Turn off") : SRApi.T("开启", "Turn on"),
                            SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(84)), GUILayout.Height(SRApi.Ctl.Sc(26)))) {
                        CrossPlay.Write(!on);
                        SRApi.Notify(CrossPlay.Note);
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

            } catch (Exception __ex) { SRApi.Guard.Log("其它页 渲染", __ex); }
        }
        // 本页功能的补丁统一注册点（CreateAndPatchAll 不会自动发现带 [HarmonyPatch] 的类型）。
        internal static void InitCleanup(IFeatureHost plugin) {
            try { Harmony.CreateAndPatchAll(typeof(Experiments), (string)null); }
            catch (Exception e) { SRApi.LogError("实验页 补丁注册失败: " + e.Message); }
        }

}

    // 自由相机（实验）：经游戏自己的 ZoomCamera 生效，不直接写 Camera.fieldOfView（会被每帧覆盖）。
    // 默认键 F3，滚轮调 FOV（1-32，每格 0.5）；仅开启时消费滚轮，每次启用从相机当前 FOV 起算，关闭时还原。
    [SrFeature("sr.freecam", "FreeCam", "1.0")]
    public class FreeCam : ITweak {
        internal const float MinFov = 1f, MaxFov = 32f;
        // 滚轮灵敏度：perspective 与 orthographic 两分支统一使用。
        private const float ZoomSensitivity = 0.5f;
        private static ConfigEntry<bool> _on;
        private static ConfigEntry<float> _fov;
        private static ConfigEntry<KeyCode> _key;
        // 相机移动速度（改 ZoomCamera.MaxFollowSpeed）。
        private static ConfigEntry<float> _followSpeed;
    
        private static void SelfReg2() {
            SRApi.LocKey("Experiments", "Free Camera", "自由相机", null);
            SRApi.LocDesc("Experiments", "Free Camera", "自由相机\n开启后可用滚轮缩放视野\n菜单与暂停界面不生效", "Free camera\nwheel zoom for the field of view\nnot applied in menus or the pause screen");
            SRApi.LocKey("Experiments", "Free Camera FOV", "视野大小", null);
            SRApi.LocDesc("Experiments", "Free Camera FOV", "自由相机视野\n越小越近，越大越远\n范围 1-32；自由相机开启时滚轮也会改", "Free camera FOV\nsmaller is closer, larger is farther\n1-32; the wheel also changes it while on");
            // 必须登记 LocKey/LocDesc，否则界面回退显示英文键名。
            SRApi.LocKey("Experiments", "Free Camera Speed", "相机移动速度", null);
            SRApi.LocDesc("Experiments", "Free Camera Speed", "相机跟随速度\n相机跟随角色的快慢\n范围 0.5-5，步长 0.5", "Camera follow speed\nhow fast the camera follows the character\n0.5-5 in steps of 0.5");
            SRApi.LocKey("Experiments", "Free Camera Key", "视野快捷键", null);
            SRApi.LocDesc("Experiments", "Free Camera Key", "自由相机按键\n按下开关自由相机\n可改绑，支持组合键", "Free camera key\ntoggles the free camera\nrebindable, combos supported");
            SRApi.RowSliders.Add(FovSlider);                       // FOV 用滑块渲染
            SRApi.RowSliders.Add(SpeedSlider);                     // 相机移动速度用滑块渲染
            SRApi.RowCompanions.Add(CameraCompanion);              // 自由相机行右侧并排快捷键框
            SRApi.RowFilters.Add(CameraRowFilter);                 // 快捷键不单独成行
        }
        private static bool FovSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = MinFov; max = MaxFov; fmt = "0";
            try { return e.Definition.Section == "Experiments" && e.Definition.Key == "Free Camera FOV"; } catch { return false; }
        }
        // 相机移动速度用滑块渲染（范围 0-10）。
        private static bool SpeedSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 10f; fmt = "0.0";
            try { return e.Definition.Section == "Experiments" && e.Definition.Key == "Free Camera Speed"; } catch { return false; }
        }
        // 量化到 0.5 一档（滑块本身按 0.1 粒度跟手）。
        private static float QuantSpeed(float v) { return Mathf.Round(v / 0.5f) * 0.5f; }
        private static string CameraCompanion(ConfigEntryBase e) {
            try { if (e.Definition.Section != "Experiments") return null; return e.Definition.Key == "Free Camera" ? "Free Camera Key" : null; } catch { return null; }
        }
        private static bool CameraRowFilter(ConfigEntryBase e) {
            try { return e.Definition.Section != "Experiments" || e.Definition.Key != "Free Camera Key"; } catch { return true; }
        }
    
        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg2();
                _on = plugin.Config.Bind("Experiments", "Free Camera", false, "自由相机：开启后滚轮缩放视野；关闭后完全恢复游戏默认相机（仅自由模式）。");
                _fov = plugin.Config.Bind("Experiments", "Free Camera FOV", 10f, new ConfigDescription("视野 1-32", new AcceptableValueRange<float>(MinFov, MaxFov)));
                _key = plugin.Config.Bind("Experiments", "Free Camera Key", KeyCode.F3, "按键切换自由相机（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）。");
                // 只受模组总开关限制，不受「自由相机」开关影响（它改的是游戏相机跟随速度）。
                _followSpeed = plugin.Config.Bind("Experiments", "Free Camera Speed", 1f,
                    new ConfigDescription("相机跟随角色的速度倍数（0-10，每次 0.5，1 = 游戏原值；与自由相机开关无关）",
                        new AcceptableValueRange<float>(0f, 10f)));
                SRApi.RegisterKey("实验-自由相机", _key, "press");
                SRApi.RegisterTick("KEY_FREECAM", CheckKey);
                SRApi.RegisterWheelHook(WheelZoom);
                // 启动时不再"强制重置并写盘"（评审 G8：会覆盖用户手动保存的设置，且每次启动都写 cfg）。
                // 只修非法值：FOV 超出范围时回落到默认 10；开关状态完全交给用户/会话控制。
                try { if (_fov.Value <= 0f || _fov.Value > 32f) _fov.Value = 10f; } catch (Exception __fv) { SRApi.Guard.Log("自由相机/修正FOV", __fv); }
                SRApi.RegisterCameraView(ApplyToCamera);
                SRApi.RegisterTick("自由相机-移动速度", ApplyFollowSpeed);
            // 相机缓存只在换场景时失效：以前缓存为 null 就全场景 FindObjectsOfType，
            // 那是 tick 剖析里 自由相机-移动速度 峰值 4ms 的来源。
            SRApi.RegisterSceneHook(delegate { _followZc = null; });   // 自由相机/换场景清缓存
                _on.SettingChanged += OnLockChanged;
                // 不挂 SettingChanged：ApplyFollowSpeed 已作为 30 帧节流的 Tick，下一拍自然生效。
                _followSpeed.SettingChanged += (s, e) => { _followAt = -9999; _followLastMul = -1f; };
            } catch (Exception __ex) { SRApi.Guard.Log("自由相机 初始化", __ex); }
        }
    
        // 直接改 ZoomCamera.MaxFollowSpeed（FixedUpdate 里相机跟随的每帧最大位移），越大越跟得上。
        // 记录原值可还原；仅总开关开启时生效。
        private static float _followOrig = -1f;
        // "原值"与当前相机对象绑定：相机对象更换时标记为待重取。
        private static ZoomCamera _followZc;
        // 相机速度的独立状态行（写入 FreeCam.Note 会串到其它页）。
        internal static string SpeedNote = "";
        // 每帧钩子：30 帧节流；文案只在倍数变化时更新。
        private static float _followAt = -9999f;
        private static float _followLastMul = -1f;
        private static void ApplyFollowSpeed() {
            try {
                if (Time.unscaledTime - _followAt < 0.5f) return;   // 0.5s 一次足够（原先 30 帧，实测仍偏重）
                _followAt = Time.unscaledTime;
                // 每帧回调需自行判 GateMaster：不满足时还原原值并清快照，下次开启重新取。
                // 只受模组总开关限制，不受「自由相机」开关限制。
                if (!SRApi.GateMaster) {
                    if (_followOrig >= 0f) {
                        try { if (_followZc != null) _followZc.MaxFollowSpeed = _followOrig; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    }
                    _followOrig = -1f;      // 下次生效时重新取（相机可能已更换）
                    _followLastMul = -1f;   // 状态文案随之失效
                    return;
                }
                // 已知相机还活着就直接用，避免每 0.5s 一次全场景 FindObjectsOfType。
                ZoomCamera zc = _followZc;
                if (zc == null) {
                    try { ZoomCamera[] zs = SRApi.FrameObjects<ZoomCamera>(); zc = zs != null && zs.Length > 0 ? zs[0] : null; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
                if (zc == null) return;
                if (_followZc != zc) { _followZc = zc; _followOrig = -1f; }   // 相机换了就重取原值
                if (_followOrig < 0f) _followOrig = zc.MaxFollowSpeed;
                // 量化到 0.5 一档并夹取
                float mul = _followSpeed != null ? QuantSpeed(Mathf.Clamp(_followSpeed.Value, 0f, 10f)) : 1f;
                float want = _followOrig * mul;
                if (Mathf.Abs(zc.MaxFollowSpeed - want) > 0.001f) zc.MaxFollowSpeed = want;
                if (!Mathf.Approximately(_followLastMul, mul)) {       // 只在倍数变化时拼文案
                    _followLastMul = mul;
                    SpeedNote = "相机移动速度 " + mul.ToString("0.0") + "×（原值 " + ((int)_followOrig) + " -> " + ((int)want) + "）";
                }
            } catch (Exception __ex) { SRApi.Guard.Log("相机移动速度", __ex); }
        }

        // 启用时把滑条与 _savedFov 同步为相机当前视野。
        private static void OnLockChanged(object s, EventArgs e) {
            try {
                if (_on != null && _on.Value) { float v = CurrentFov(); _fov.Value = Mathf.Clamp(v, MinFov, MaxFov); _savedFov = v; Camera g = GameCamera(); if (g != null) { try { if (g.orthographic) g.orthographicSize = _fov.Value; else g.fieldOfView = _fov.Value; } catch { /* 静默：非关键路径，失败可直接忽略 */ } } }
                SRApi.Notify((_on != null && _on.Value) ? SRApi.T("自由相机：开", "Free camera: ON")
                                                 : SRApi.T("自由相机：关（已还原）", "Free camera: OFF (restored)"));
            } catch (Exception __ex) { SRApi.Guard.Log("自由相机 开关变化", __ex); }
        }
        internal static void CheckKey() {
            try {
                // 总开关必须在此判定，否则关闭模组后快捷键仍能改游戏相机。
                if (!SRApi.GateMaster) return;
                if (!SRApi.ComboKeyDown(_key)) return;
                // 自由相机全局有效，但仍只改游戏相机。
                if (_on != null) _on.Value = !_on.Value;                   // 快捷键等于勾选框
            } catch (Exception __ex) { SRApi.Guard.Log("自由相机 快捷键", __ex); }
        }
    
        // 游戏实际的渲染相机：ZoomCamera.useCamera -> 其 Camera 组件 -> 静态 CurrentZoomCamera -> Camera.main
        internal static Camera GameCamera() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    ZoomCamera zc = lm.GetCurrentZoomCamera();
                    if (zc != null) {
                        if (zc.useCamera != null) return zc.useCamera;
                        Camera c = zc.GetComponent<Camera>(); if (c != null) return c;
                    }
                }
                if (ZoomCamera.CurrentZoomCamera != null) return ZoomCamera.CurrentZoomCamera;
            } catch (Exception __ex) { SRApi.Guard.Log("获取当前 ZoomCamera", __ex); }
            return Camera.main;
        }
        public static float CurrentFov() {
            try { Camera cam = GameCamera(); if (cam == null) return _fov != null ? _fov.Value : 10f; return cam.orthographic ? cam.orthographicSize : cam.fieldOfView; } catch { return 10f; }
        }
        // 滚轮缩放。
        public static bool WheelZoom(float wheel) {
            try {
                // 总开关关闭时不消费滚轮，避免与其它功能抢同一事件。
                if (!SRApi.GateMaster) return false;
                if (_on == null || !_on.Value || Mathf.Abs(wheel) < 0.001f) return false;
                bool inGame = false;
                try { inGame = (UnityEngine.Cursor.lockState != CursorLockMode.None) || !UnityEngine.Cursor.visible; } catch { inGame = true; }
                if (!inGame) return false;                       // 菜单里不抢滚轮
                Camera cam = GameCamera(); if (cam == null) return false;
                if (cam.orthographic) _fov.Value = Mathf.Clamp(cam.orthographicSize - wheel * ZoomSensitivity, MinFov, MaxFov);
                else _fov.Value = Mathf.Clamp(cam.fieldOfView - wheel * ZoomSensitivity, MinFov, MaxFov);
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("自由相机 滚轮", __ex); return false; }
        }
        // 每帧幂等：满足条件时相机 FOV 跟随滑条值。
        private static float _savedFov = -1f;   // 开启时的原视野，关闭/菜单场景还原用
        // 回调会轮到菜单/UI 相机，故三道闸：开关开、只改游戏相机（cam == GameCamera()）、仅游戏进行中；
        // 任一不满足时还原已改过的 FOV，确保菜单渲染正常。
        public static void ApplyToCamera(Camera cam) {
            try {
                if (cam == null) return;
                Camera g = GameCamera();
                if (g == null || cam != g) return;
                // 未开启（含总开关关闭）：写回保存的原视野后返回。
                // 核心的 ApplyCameraViews 分发不带门控，本回调需自行判定总开关。
                if (!SRApi.GateMaster || _on == null || !_on.Value) {
                    RestoreFov(cam);
                    return;
                }
                // 仅游戏进行中，菜单里也还原
                bool inGame = false;
                try { inGame = (UnityEngine.Cursor.lockState != CursorLockMode.None) || !UnityEngine.Cursor.visible; } catch { inGame = true; }
                if (!inGame) { RestoreFov(cam); return; }
                if (cam.orthographic) cam.orthographicSize = Mathf.Clamp(_fov.Value, MinFov, MaxFov);
                else cam.fieldOfView = Mathf.Clamp(_fov.Value, MinFov, MaxFov);
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }
        // 还原开启时抓到的原视野并清掉标记（两个早退分支共用）。
        private static void RestoreFov(Camera cam) {
            if (_savedFov <= 0f || cam == null) return;
            try { if (cam.orthographic) cam.orthographicSize = _savedFov; else cam.fieldOfView = _savedFov; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            _savedFov = -1f;
        }
    }
    // 实验 · 跨平台联机：反射定位游戏自己的 CrossPlatformToggle，读/写/尝试让游戏落盘。
    internal static class CrossPlay {
        private const string MemberName = "CrossPlatformToggle";
        private static MemberInfo _member;      // FieldInfo 或 PropertyInfo
        private static object _owner;           // 成员是实例成员时的宿主对象（null = 静态成员）
        private static bool _probed;
        internal static string Note = "";

        // 初始化入口（由 Experiments.Initialize 调用；UI 侧也会惰性 Probe）。
        internal static void Init() {
            try { Probe(); } catch (Exception __ex) { SRApi.Guard.Log("跨平台联机/探测", __ex); }
        }

        // 找成员 + 宿主：优先静态成员；否则在单例（instance / Instance / Current）上取。
        private static void Probe() {
            if (_probed) return;
            _probed = true;
            try {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < asms.Length; a++) {
                    Assembly asm = asms[a];
                    if (asm == null || asm.IsDynamic) continue;
                    string an = "";
                    try { an = asm.GetName().Name; } catch { continue; }
                    if (an != "Assembly-CSharp") continue;   // 只认游戏本体，避免撞到别的模组
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException __tl) { types = __tl.Types; }
                    catch (Exception __t) { SRApi.Guard.Log("跨平台联机/取类型", __t); continue; }
                    if (types == null) continue;
                    // 优先 GameSettings（真正的运行时设置对象），其次才任意类型；
                    // 以前按 asm.GetTypes() 顺序取第一个，常常先命中 SaveFileData（没有 GetInstance），
                    // 于是宿主永远解析不到 → "游戏还没初始化完" 一直出现。
                    Type prefer = null;
                    for (int ti = 0; ti < types.Length; ti++) {
                        if (types[ti] != null && types[ti].FullName == "GameSettings") { prefer = types[ti]; break; }
                    }
                    int passCount = prefer != null ? 2 : 1;
                    for (int pass = 0; pass < passCount && _member == null; pass++) {
                        Type[] scan = pass == 0 && prefer != null ? new Type[] { prefer } : types;
                        for (int t = 0; t < scan.Length; t++) {
                            Type ty = scan[t];
                            if (ty == null) continue;
                            try {
                                FieldInfo f = ty.GetField(MemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                                if (f != null && f.FieldType == typeof(bool)) {
                                    object owner = f.IsStatic ? null : FindOwner(ty);
                                    if (f.IsStatic || owner != null) { _member = f; _owner = owner; break; }
                                }
                                PropertyInfo pr = ty.GetProperty(MemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                                if (pr != null && pr.PropertyType == typeof(bool) && pr.CanRead) {
                                    MethodInfo g = pr.GetGetMethod(true);
                                    bool st = g != null && g.IsStatic;
                                    object owner = st ? null : FindOwner(ty);
                                    if (st || owner != null) { _member = pr; _owner = owner; break; }
                                }
                            } catch (Exception __m) { SRApi.Guard.Log("跨平台联机/读成员 " + ty.Name, __m); }
                        }
                    }
                    if (_member != null) break;
                }
                if (_member == null) SRApi.LogWarn("[跨平台] 没找到 CrossPlatformToggle（游戏版本可能改了名），开关将不可用");
                else SRApi.LogInfo("[跨平台] 已定位 " + _member.DeclaringType.FullName + "." + _member.Name
                    + (_owner != null ? "（实例）" : "（静态）") + "，当前值=" + (Read() ? "开" : "关"));
            } catch (Exception __ex) { SRApi.Guard.Log("跨平台联机/探测成员", __ex); }
        }

        // 单例查找：常见命名 instance / Instance / Current / singleton。
        private static object FindOwner(Type ty) {
            // 先试 GetInstance()（UCH 的 GameSettings 就是用这个而不是 instance 字段）。
            try {
                System.Reflection.MethodInfo gi = ty.GetMethod("GetInstance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, System.Type.EmptyTypes, null);
                if (gi != null) {
                    object v = gi.Invoke(null, null);
                    if (v != null) return v;
                }
            } catch (Exception __gi) { SRApi.Guard.Log("跨平台联机/GetInstance", __gi); }
            string[] names = new string[] { "instance", "Instance", "Current", "current", "singleton", "Singleton" };
            for (int i = 0; i < names.Length; i++) {
                try {
                    FieldInfo f = ty.GetField(names[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null && typeof(object).IsAssignableFrom(f.FieldType)) {
                        object v = f.GetValue(null);
                        if (v != null) return v;
                    }
                    PropertyInfo pr = ty.GetProperty(names[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (pr != null && pr.CanRead) {
                        object v = pr.GetValue(null, null);
                        if (v != null) return v;
                    }
                } catch (Exception __f) { SRApi.Guard.Log("跨平台联机/找单例 " + names[i], __f); }
            }
            return null;
        }

        internal static bool Available { get { Probe(); return _member != null; } }

        // 宿主按需解析：GameSettings 是 ScriptableObject 单例，插件初始化时 GetInstance() 往往还是 null，
        // 之前把它缓存成"静态成员"处理，导致读/写都抛 "Non-static field requires a target"。
        private static object CurrentOwner(out bool isStaticField) {
            isStaticField = true;
            try {
                if (_member == null) return null;
                FieldInfo f = _member as FieldInfo;
                if (f != null) {
                    isStaticField = f.IsStatic;
                    if (f.IsStatic) return null;
                } else {
                    PropertyInfo pr = (PropertyInfo)_member;
                    MethodInfo g = pr.GetGetMethod(true);
                    isStaticField = g != null && g.IsStatic;
                    if (isStaticField) return null;
                }
                if (_owner == null) _owner = FindOwner(_member.DeclaringType);
                return _owner;
            } catch (Exception __co) { SRApi.Guard.Log("跨平台联机/解析宿主", __co); return null; }
        }

        internal static bool Read() {
            try {
                if (_member == null) return false;
                bool isStatic;
                object owner = CurrentOwner(out isStatic);
                if (!isStatic && owner == null) return false;   // 游戏还没实例化：静默当"关"，不报异常
                FieldInfo f = _member as FieldInfo;
                if (f != null) return (bool)f.GetValue(f.IsStatic ? null : owner);
                PropertyInfo pr = _member as PropertyInfo;
                if (pr != null && pr.CanRead) return (bool)pr.GetValue(isStatic ? null : owner, null);
            } catch (Exception __ex) { SRApi.Guard.Log("跨平台联机/读值", __ex); }
            return false;
        }

        // 写值 + 让游戏自己落盘（能存上才算真的改了；存不上就提示）。
        internal static bool Write(bool on) {
            try {
                if (_member == null) return false;
                bool isStatic;
                object resolved = CurrentOwner(out isStatic);
                if (!isStatic && resolved == null) {
                    Note = SRApi.T("游戏还没初始化完，稍后再试", "The game is not initialised yet; try again");
                    return false;
                }
                object target = isStatic ? null : resolved;
                FieldInfo fl = _member as FieldInfo;
                if (fl != null) fl.SetValue(fl.IsStatic ? null : target, on);
                else {
                    PropertyInfo pr = (PropertyInfo)_member;
                    if (!pr.CanWrite) { Note = SRApi.T("该设置不可写（只读属性）", "read-only property"); return false; }
                    pr.SetValue(isStatic ? null : target, on, null);
                }
                // 只写 GameSettings 只在本次会话生效；存档里的真值是 SaveFileData.CrossPlatformToggle，
                // 通过 StatTracker 拿到主用户存档后同步写一次，再让游戏自己保存。
                bool savedFile = TryWriteSaveFile(on);
                bool persisted = TryPersist(target) || savedFile;
                Note = (on ? SRApi.T("已开启跨平台联机", "Cross-play ON") : SRApi.T("已关闭跨平台联机", "Cross-play OFF"))
                    + (persisted ? "" : SRApi.T("（未能确认写入存档，重启游戏可能回退）", " (could not confirm it was saved; may revert on restart)"));
                SRApi.LogInfo("[跨平台] " + Note);
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("跨平台联机/写值", __ex); return false; }
        }

        // 同步写存档对象：StatTracker.Instance.GetSaveFileDataForMainUser().CrossPlatformToggle。
        private static bool TryWriteSaveFile(bool on) {
            try {
                Type st = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    try {
                        if (asm.GetName().Name != "Assembly-CSharp") continue;
                        st = asm.GetType("StatTracker", false);
                    } catch (Exception __a) { SRApi.Guard.Log("跨平台联机/找 StatTracker", __a); }
                    if (st != null) break;
                }
                if (st == null) return false;
                object tracker = null;
                try {
                    MethodInfo gi = st.GetMethod("GetInstance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (gi != null) tracker = gi.Invoke(null, null);
                } catch (Exception __gi) { SRApi.Guard.Log("跨平台联机/StatTracker 实例", __gi); }
                if (tracker == null) {
                    try {
                        PropertyInfo pi = st.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (pi != null) tracker = pi.GetValue(null, null);
                    } catch (Exception __pi) { SRApi.Guard.Log("跨平台联机/StatTracker.Instance", __pi); }
                }
                if (tracker == null) return false;
                MethodInfo get = st.GetMethod("GetSaveFileDataForMainUser",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (get == null) return false;
                object data = get.Invoke(tracker, null);
                if (data == null) return false;
                FieldInfo f = data.GetType().GetField("CrossPlatformToggle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null || f.FieldType != typeof(bool)) return false;
                f.SetValue(data, on);
                try { TryPersist(tracker); } catch (Exception __p) { SRApi.Guard.Log("跨平台联机/保存 StatTracker", __p); }
                SRApi.LogInfo("[跨平台] 已同步写入存档字段 CrossPlatformToggle=" + (on ? "true" : "false"));
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("跨平台联机/写存档", __ex); return false; }
        }

        // 找游戏自己的保存入口：方法名包含 save/write/flush 且无参、返回 void 或 bool。
        private static bool TryPersist(object owner) {
            if (owner == null) return false;
            try {
                Type ty = owner.GetType();
                MethodInfo[] ms = ty.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                string[] keys = new string[] { "SaveData", "WriteSave", "SaveToDisk", "Save", "Write", "Flush", "Persist" };
                for (int k = 0; k < keys.Length; k++) {
                    for (int i = 0; i < ms.Length; i++) {
                        MethodInfo m = ms[i];
                        if (m == null || m.IsAbstract || m.GetParameters().Length != 0) continue;
                        string n = m.Name;
                        // 只接受"名字完全等于白名单项"且声明在目标类型自身上的方法：
                        // 以前用"名字包含 save/write/flush"做子串匹配 + 继承链搜索，
                        // 可能调到 SaveSettingsAndQuit 之类的破坏性方法（audit 20-P0-1）。
                        if (!string.Equals(n, keys[k], StringComparison.Ordinal)) continue;
                        if (m.DeclaringType != ty) continue;
                        if (m.ReturnType != typeof(void) && m.ReturnType != typeof(bool)) continue;
                        try {
                            object r = m.Invoke(m.IsStatic ? null : owner, null);
                            if (m.ReturnType == typeof(bool) && !(bool)r) continue;
                            SRApi.LogInfo("[跨平台] 已调用保存入口 " + ty.Name + "." + n + "(" + (m.GetParameters().Length) + " 参)");
                            return true;
                        } catch (Exception __i) { SRApi.Guard.Log("跨平台联机/调用 " + ty.Name + "." + n, __i); }
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("跨平台联机/找保存入口", __ex); }
            return false;
        }
    }
}
