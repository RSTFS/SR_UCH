// 实验 · 其它页：自由相机 + 加载后清理（GC）。

using System;
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

				// 加载后清理
				GUILayout.Space(SRApi.Ctl.Sc(6));
				SRApi.Ctl.SectionTitle("- " + SRApi.T("加载后清理", "Cleanup after load") + " -", SRApi.T("进关卡时执行一次垃圾回收和资源卸载. 减少对局内卡顿. 不影响结算速度.",
					 "Runs GC and asset unload once on level load, reducing in-match stutter. Does not affect scoring."));
				SRApi.RenderSectionEntries("Experiments", delegate (ConfigEntryBase e) {
					return e.Definition.Key == "GC After Load";
				});
				GUILayout.Space(SRApi.Ctl.Sc(6));
			} catch (Exception __ex) { SRApi.Guard.Log("其它页 渲染", __ex); }
		}
		// 加载后清理的初始化：配置 + 每帧钩子 + 补丁注册（UI 在上面的加载后清理分区）。
		internal static void InitCleanup(IFeatureHost plugin) {
			SRApi.LocKey("Experiments", "GC After Load", "加载后清理", "GC after load");
			SRApi.LocDesc("Experiments", "GC After Load", "加载后清理开关\n首次进入关卡时执行一次 GC 与资源卸载，减少对局内卡顿\n同一关卡的回合切换不重复执行，不影响结算速度\n关", "post-load cleanup switch\nruns GC and asset unload once on level load to reduce in-match stutter\nskipped on round changes within the same level; settlement speed unaffected\nOFF");
			_gcAfterLoadEntry = plugin.Config.Bind<bool>("Experiments", "GC After Load", false, "进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。");
			SRApi.RegisterTick("加载后清理", TickCleanup); // 每帧钩子（加载完成 1 秒后执行一次 GC）
			// CreateAndPatchAll 不会自动发现带 [HarmonyPatch] 的类型，必须显式注册一次。
			try { Harmony.CreateAndPatchAll(typeof(Experiments), (string)null); }
			catch (Exception e) { SRApi.LogError("实验页(加载后清理) 补丁注册失败: " + e.Message); }
		}

		// 分区：加载后清理（UI 在本页，配置与实现同在本文件）。
		private static ConfigEntry<bool> _gcAfterLoadEntry;
		// 加载完成时回收垃圾，减少进入对局后的卡顿；同场景（回合切换）跳过。
		// 延迟 1 秒执行，避免 GC 的同步阻塞卡住淡出过渡。
		private static string _lastCleanedScene = "";
		private static float _pendingCleanupAt = -1f;
		private static string _pendingCleanupScene = "";
		// 加载后清理的实际执行（SR 每帧钩子 每帧钩子）：FadeOut 后延迟 1 秒再 GC。
		internal static void TickCleanup()
		{
			// 与记录处对称：总开关关闭时不执行。
			if (!SRApi.GateMaster) return;
			if (_pendingCleanupAt < 0f || Time.unscaledTime < _pendingCleanupAt) return;
			_pendingCleanupAt = -1f;
			try
			{
				_lastCleanedScene = _pendingCleanupScene;
				System.GC.Collect();
				Resources.UnloadUnusedAssets();
			}
			catch (Exception __ex) { SRApi.Guard.Log("加载后清理(GC)", __ex); }
		}
		// 加载过渡结束（FadeOut 后缀）：记录待清理场景与 1 秒后的执行时刻。
		[HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
		[HarmonyPostfix]
		static void OnLoadEndRecordCleanup()
		{
			if (!SRApi.GateMaster) return;
			if (_gcAfterLoadEntry == null || !_gcAfterLoadEntry.Value) return;
			try
			{
				string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
				if (sc == _lastCleanedScene) return; // 同场景（回合切换重载）不清理
				_pendingCleanupScene = sc;
				_pendingCleanupAt = Time.unscaledTime + 1f;
			}
			catch (Exception __ex) { SRApi.Guard.Log("记录加载后清理场景", __ex); }
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
            SRApi.LocDesc("Experiments", "Free Camera", "自由相机开关\n开启后可用滚轮缩放视野\n菜单与暂停界面不生效\n关", "free camera switch\nenables wheel zoom of the field of view\ndoes not apply in menus or the pause screen\nOFF");
            SRApi.LocKey("Experiments", "Free Camera FOV", "视野大小", null);
            SRApi.LocDesc("Experiments", "Free Camera FOV", "自由相机视野大小\n设置视野范围，越小越近，越大越远\n范围 1 到 32；自由相机开启时滚轮也会同步修改\n10", "free camera FOV\nsets the field of view; smaller is closer, larger is farther\nrange 1-32; the wheel also changes it while free camera is on\n10");
            // 必须登记 LocKey/LocDesc，否则界面回退显示英文键名。
            SRApi.LocKey("Experiments", "Free Camera Speed", "相机移动速度", null);
            SRApi.LocDesc("Experiments", "Free Camera Speed", "相机跟随速度\n设置相机跟随角色的速度\n范围 0.5 到 5，步长 0.5\n1（原版）", "camera follow speed\nsets how fast the camera follows the character\nrange 0.5-5 in steps of 0.5\n1 (vanilla)");
            SRApi.LocKey("Experiments", "Free Camera Key", "视野快捷键", null);
            SRApi.LocDesc("Experiments", "Free Camera Key", "自由相机快捷键\n按该键开关自由相机\n可改绑，支持组合键\nF3", "free camera key\ntoggles the free camera\nrebindable, combos supported\nF3");
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
                SRApi.RegisterTick("自由相机-开关", CheckKey);
                SRApi.RegisterWheelHook(WheelZoom);
                // 启动时强制重置为默认状态：关闭自由相机、FOV 回 10（赋值会写盘，属 BepInEx 行为）。
                _on.Value = false;
                _fov.Value = 10f;
                SRApi.RegisterCameraView(ApplyToCamera);
                SRApi.RegisterTick("自由相机-移动速度", ApplyFollowSpeed);
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
        private static int _followAt = -9999;
        private static float _followLastMul = -1f;
        private static void ApplyFollowSpeed() {
            try {
                if (Time.frameCount - _followAt < 30) return;   // 30 帧一次足够
                _followAt = Time.frameCount;
                // 每帧回调需自行判 GateMaster：不满足时还原原值并清快照，下次开启重新取。
                // 只受模组总开关限制，不受「自由相机」开关限制。
                if (!SRApi.GateMaster) {
                    if (_followOrig >= 0f) {
                        try { if (_followZc != null) _followZc.MaxFollowSpeed = _followOrig; } catch { }
                    }
                    _followOrig = -1f;      // 下次生效时重新取（相机可能已更换）
                    _followLastMul = -1f;   // 状态文案随之失效
                    return;
                }
                ZoomCamera zc = null;
                try { zc = UnityEngine.Object.FindObjectOfType<ZoomCamera>(); } catch { }
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
                if (_on != null && _on.Value) { float v = CurrentFov(); _fov.Value = Mathf.Clamp(v, MinFov, MaxFov); _savedFov = v; Camera g = GameCamera(); if (g != null) { try { if (g.orthographic) g.orthographicSize = _fov.Value; else g.fieldOfView = _fov.Value; } catch { } } }
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
            } catch { }
        }
        // 还原开启时抓到的原视野并清掉标记（两个早退分支共用）。
        private static void RestoreFov(Camera cam) {
            if (_savedFov <= 0f || cam == null) return;
            try { if (cam.orthographic) cam.orthographicSize = _savedFov; else cam.fieldOfView = _savedFov; } catch { }
            _savedFov = -1f;
        }
    }
}
