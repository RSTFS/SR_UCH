// 实验 · 其它页。自由相机 + 加载后清理（GC）。
// 整个实验栏目的总览看同目录的 Experiments.cs。

using System;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
	{
		// **其它页**：自由相机 / 加载后清理
		public static void RenderPage2() {
			try {
				// 自由相机
				GUILayout.Label(new GUIContent("- " + SR.T("自由相机", "Free camera") + " -",
					SR.T("开启后可以用滚轮自由缩放视野. 关闭后完全恢复游戏默认相机. 只在自由模式有效.",
					 "While on, the wheel zooms the view freely. Off = fully restores the default camera. Freeplay only.")),
					SR.Ctl.SecHeader);
				SR.RenderSectionEntries("Experiments", delegate (ConfigEntryBase e) {
					// **R394**：相机移动速度归到自由相机分区（用户要求）,
					//  它本来就只对自由相机生效，放在场景页是错位。
					return e.Definition.Key == "Free Camera" || e.Definition.Key == "Free Camera FOV"
						|| e.Definition.Key == "Free Camera Speed";
				});
				if (FreeCam.SpeedNote != null && FreeCam.SpeedNote.Length > 0) {
					GUILayout.Space(SR.Ctl.Sc(2));
					DrawStatus(FreeCam.SpeedNote);
				}

				// 加载后清理
				GUILayout.Space(SR.Ctl.Sc(6));
				GUILayout.Label(new GUIContent("- " + SR.T("加载后清理", "Cleanup after load") + " -",
					SR.T("进关卡时执行一次垃圾回收和资源卸载. 减少对局内卡顿. 不影响结算速度.",
					 "Runs GC and asset unload once on level load, reducing in-match stutter. Does not affect scoring.")),
					SR.Ctl.SecHeader);
				SR.RenderSectionEntries("Experiments", delegate (ConfigEntryBase e) {
					return e.Definition.Key == "GC After Load";
				});
				GUILayout.Space(SR.Ctl.Sc(6));
			} catch (Exception __ex) { SR.Guard.Log("其它页 渲染", __ex); }
		}
		//加载后清理的初始化（配置 + 每帧钩子 + 补丁注册；UI 就在上面的加载后清理分区里）
		internal static void InitCleanup(IFeatureHost plugin) {
			SR.LocKey("Experiments", "GC After Load", "加载后清理", "GC after load");
			SR.LocDesc("Experiments", "GC After Load",
				"进关卡的时候回收一次垃圾和没用的资源, 对局里会少卡一点. 只在第一次加载后做, 同关卡换回合不做.",
				"Run GC and asset unload once on level load, reducing in-match stutter.");
			_gcAfterLoadEntry = plugin.Config.Bind<bool>("Experiments", "GC After Load", false, "进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。");
			SR.RegisterTick("加载后清理", TickCleanup); //每帧钩子（加载完成 1 秒后执行一次 GC）
			//注册本 partial 类里的补丁（本文件 OnLoadEndRecordCleanup）：
			//CreateAndPatchAll 不会自动发现带 [HarmonyPatch] 的类型，必须显式注册一次。
			try { Harmony.CreateAndPatchAll(typeof(Experiments), (string)null); }
			catch (Exception e) { SR.LogError("实验页(加载后清理) 补丁注册失败: " + e.Message); }
		}

		// 分区：加载后清理（UI 在本页 -> 配置 + 实现都在本文件）
		//原则：功能跟着 UI 走, 界面显示在哪个页面，它的配置与实现就归哪个文件，别处不再反向引用。
		private static ConfigEntry<bool> _gcAfterLoadEntry;
		//加载完成：回收加载产生的垃圾，减少进入对局后的卡顿；只在本场景第一次加载完成时清理
		//（同关卡回合切换场景名不变 -> 跳过，避免每回合 GC 拖慢结算）。延迟 1 秒执行，避免 GC 的同步阻塞卡住淡出过渡。
		private static string _lastCleanedScene = "";
		private static float _pendingCleanupAt = -1f;
		private static string _pendingCleanupScene = "";
		//加载后清理的实际执行（由 SR.Tick 每帧钩子调用）：FadeOut 后延迟 1 秒再 GC，不阻塞过渡
		internal static void TickCleanup()
		{
			// **与记录处对称**：记录时判了 GateMaster，执行时也必须判, 否则"记录完立刻关总开关"
			//  仍会在 1 秒后跑一次全量 GC + 资源卸载（用户此时已明确关闭全部功能）。
			if (!SR.GateMaster) return;
			if (_pendingCleanupAt < 0f || Time.unscaledTime < _pendingCleanupAt) return;
			_pendingCleanupAt = -1f;
			try
			{
				_lastCleanedScene = _pendingCleanupScene;
				System.GC.Collect();
				Resources.UnloadUnusedAssets();
			}
			catch (Exception __ex) { SR.Guard.Log("加载后清理(GC)", __ex); }
		}
		//加载过渡结束（游戏自己的 FadeOut 后缀）：记下"待清理场景 + 1 秒后清理"
		[HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
		[HarmonyPostfix]
		static void OnLoadEndRecordCleanup()
		{
			if (!SR.GateMaster) return;
			if (_gcAfterLoadEntry == null || !_gcAfterLoadEntry.Value) return;
			try
			{
				string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
				if (sc == _lastCleanedScene) return; //同场景（回合切换重载）不清理
				_pendingCleanupScene = sc;
				_pendingCleanupAt = Time.unscaledTime + 1f;
			}
			catch (Exception __ex) { SR.Guard.Log("记录加载后清理场景", __ex); }
		}
}
    // 自由相机（实验）,  走**游戏自己的相机控制器 ZoomCamera**
    //   （不要直接写 Camera.fieldOfView：FOV 由 ZoomCamera 每帧决定，写相机组件会被立刻覆盖）。
    //   照原源码 Camera.cs(FovAdjust)：相机取自 LobbyManager.GetCurrentZoomCamera() -> zc.useCamera
    //   -> zc.GetComponent<Camera>() -> ZoomCamera.CurrentZoomCamera（静态）-> Camera.main。
    //   · 开关默认 F3；开启后滚轮调 FOV（1-32，每格 0.5），每次启用从相机当前 FOV 起算，关闭时还原；
    //   · 滚轮只在开启时消费（不与页面滚动/表格抢）；总开关关闭时一律不生效；
    //   · 相机模式**不碰**（曾经设过 freeFormCamEnabled，实测会让菜单异常）。
    public class FreeCam : ITweak {
        internal const float MinFov = 1f, MaxFov = 32f;
        // **按需求调低滚轮灵敏度**：原来 perspective 用 7、orthographic 另硬编码 5，一格滚轮跳得太多。
        //  现在两分支统一用它，值 = 0.5（一格滚轮约 0.5 度/单位）。
        private const float ZoomSensitivity = 0.5f;
        private static ConfigEntry<bool> _on;
        private static ConfigEntry<float> _fov;
        private static ConfigEntry<KeyCode> _key;
        // **R392 新增**：相机移动速度（调 ZoomCamera.MaxFollowSpeed，原文 public float 149285）
        private static ConfigEntry<float> _followSpeed;
    
        private static void SelfReg2() {
            SR.LocKey("Experiments", "Free Camera", "自由相机", null);
            SR.LocDesc("Experiments", "Free Camera", "开了以后滚轮能自由缩放视野, 1 到 32. 关掉就完全交回游戏. 菜单和暂停界面里不生效, 因为那时候光标是可见的.", "Free camera: wheel zooms the FOV (1-32); works in all modes (menus excluded). Still bound to the mod master switch.");
            SR.LocKey("Experiments", "Free Camera FOV", "视野大小", null);
            SR.LocDesc("Experiments", "Free Camera FOV", "视野范围, 1 到 32. 数字越小拉得越近, 越大看得越远. 自由相机开着的时候滚轮也会同步改它.", "FOV 1-32.");
            // **R394 补登记**：R392 加 Bind 时漏了 LocKey/LocDesc -> 界面上显示的是英文键名
            //  "Free Camera Speed"（ZhKey 查不到就退回 Definition.Key）。
            SR.LocKey("Experiments", "Free Camera Speed", "相机移动速度", null);
            SR.LocDesc("Experiments", "Free Camera Speed",
                "相机追角色有多快, 0.5 到 5, 每次 0.5. 1 是原版. 角色跑太快相机跟不上就把它调大.",
                "How fast the camera follows the character: 0.5 to 5 in steps of 0.5. 1 = the game default.");
            SR.LocKey("Experiments", "Free Camera Key", "视野快捷键", null);
            SR.LocDesc("Experiments", "Free Camera Key", "按这个键开关自由相机. 想用组合键就点按钮, 按住 Shift 或 Ctrl 再按主键.", "Key to toggle the free camera.");
            SR.RowSliders.Add(FovSlider);                       //FOV 用滑块渲染
            SR.RowSliders.Add(SpeedSlider);                     // **R413**：相机移动速度也用滑块渲染
            SR.RowCompanions.Add(CameraCompanion);              //自由相机行右侧并排它的快捷键框
            SR.RowFilters.Add(CameraRowFilter);                 //快捷键不再单独成行
        }
        private static bool FovSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = MinFov; max = MaxFov; fmt = "0";
            try { return e.Definition.Section == "Experiments" && e.Definition.Key == "Free Camera FOV"; } catch { return false; }
        }
        // **R413**：相机移动速度从编辑框改成滑块（用户要求）。范围 0.5-5、每次 0.5。
        //  原来只有 AcceptableValueRange(0.2,5) + 编辑框，输错就跳值、手感差。
        private static bool SpeedSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0.5f; max = 5f; fmt = "0.0";
            try { return e.Definition.Section == "Experiments" && e.Definition.Key == "Free Camera Speed"; } catch { return false; }
        }
        //滑块步进 0.5（滑块本身按 0.1 粒度跟手，提交时量化到 0.5）
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
                _followSpeed = plugin.Config.Bind("Experiments", "Free Camera Speed", 1f,
                    // **R413**：范围收成 0.5-5（用户指定），步进 0.5 由 SpeedSlider 的 fmt + QuantSpeed 控制
                    new ConfigDescription("相机跟随角色的速度倍数（0.5-5，每次 0.5，1 = 游戏原值）", new AcceptableValueRange<float>(0.5f, 5f)));
                SR.RegisterKey("实验-自由相机", _key, "press");
                SR.RegisterTick("自由相机-开关", CheckKey);
                SR.RegisterWheelHook(WheelZoom);
                // **需求**：启动时自由相机必须是关的、FOV 回 10（这两项是运行时状态，不该跨启动残留）。
                //  注意 ConfigEntry.Value 赋值本身是会写盘的（BepInEx 行为），所以这里是"**启动时强制
                //  重置为默认状态**"，不是"不写盘"——旧注释写成"状态不写盘"是错的。
                _on.Value = false;
                _fov.Value = 10f;
                SR.RegisterCameraView(ApplyToCamera);
                SR.RegisterTick("自由相机-移动速度", ApplyFollowSpeed);
                _on.SettingChanged += OnLockChanged;
                // **R414**：不再挂 SettingChanged。ApplyFollowSpeed 已作为 Tick 注册（30 帧节流），
                // 改完配置下一拍自然生效；事件驱动会和拖动抢，把提交信号冲掉。
                _followSpeed.SettingChanged += (s, e) => { _followAt = -9999; _followLastMul = -1f; };
            } catch (Exception __ex) { SR.Guard.Log("自由相机 初始化", __ex); }
        }
    
        // **R392相机移动速度**：直接改 ZoomCamera.MaxFollowSpeed（public float，149285）。
        //  它是 FixedUpdate 里相机跟随的**每帧最大位移**（149721-149729），越大越跟得上。
        //  记原值可还原；只在自由相机开着时生效。
        private static float _followOrig = -1f;
        // P2-26：_followOrig 原来一旦取到就**永不刷新**。换场景后 ZoomCamera 是新对象，
        //  却仍拿上一个场景记下的原值去算倍数（若两个场景的默认跟随速度不同就会被改错）。
        //  这里把"原值"跟当前相机绑定：相机对象换了就把原值标记为待重取。
        private static ZoomCamera _followZc;
        // R393：相机速度的**独立**状态行。之前它写进 FreeCam.Note，
        //  而其它页会 DrawStatus(FreeCam.Note) -> 相机速度的内容串到了别的页面（用户反馈）。
        internal static string SpeedNote = "";
        // R396 N13：本方法被注册为**每帧**钩子（RegisterTick），原来第一件事就是全场景
        //  FindObjectOfType<ZoomCamera>()，末尾还无条件拼字符串；而 SpeedNote 只被
        //  其它页的 DrawStatus 读一次（还要面板停在该页且双开关都开）-> 绝大多数帧纯浪费。
        //  改法：30 帧节流（FixedUpdate 消费，足够）+ 文案只在倍数变化时更新。
        private static int _followAt = -9999;
        private static float _followLastMul = -1f;
        private static void ApplyFollowSpeed() {
            try {
                if (Time.frameCount - _followAt < 30) return;   //30 帧一次足够
                _followAt = Time.frameCount;
                ZoomCamera zc = null;
                try { zc = UnityEngine.Object.FindObjectOfType<ZoomCamera>(); } catch { }
                if (zc == null) return;
                if (_followZc != zc) { _followZc = zc; _followOrig = -1f; }   // **P2-26**：相机换了就重新取原值
                if (_followOrig < 0f) _followOrig = zc.MaxFollowSpeed;
                // **R413**：量化到 0.5 一档并夹进 0.5-5（滑块提交的值可能是 0.1 粒度）
                float mul = _followSpeed != null ? QuantSpeed(Mathf.Clamp(_followSpeed.Value, 0.5f, 5f)) : 1f;
                float want = _followOrig * mul;
                if (Mathf.Abs(zc.MaxFollowSpeed - want) > 0.001f) zc.MaxFollowSpeed = want;
                if (!Mathf.Approximately(_followLastMul, mul)) {       //只在倍数变化时拼文案
                    _followLastMul = mul;
                    SpeedNote = "相机移动速度 " + mul.ToString("0.0") + "×（原值 " + ((int)_followOrig) + " -> " + ((int)want) + "）";
                }
            } catch (Exception __ex) { SR.Guard.Log("相机移动速度", __ex); }
        }

        // 启用时把滑条同步到"相机当前视野"（= 每次启用都从当前 FOV 为起始值）
        private static void OnLockChanged(object s, EventArgs e) {
            try {
                if (_on != null && _on.Value) { float v = CurrentFov(); _fov.Value = Mathf.Clamp(v, MinFov, MaxFov); _savedFov = v; Camera g = GameCamera(); if (g != null) { try { if (g.orthographic) g.orthographicSize = _fov.Value; else g.fieldOfView = _fov.Value; } catch { } } }
                SR.Notify((_on != null && _on.Value) ? SR.T("自由相机：开", "Free camera: ON")
                                                 : SR.T("自由相机：关（已还原）", "Free camera: OFF (restored)"));
            } catch (Exception __ex) { SR.Guard.Log("自由相机 开关变化", __ex); }
        }
        internal static void CheckKey() {
            try {
                // 总开关（GateMaster 永不豁免）：旧 Camera.cs 此处有该判据，并入本文件时遗漏。
                //  缺了它 -> 用户关掉本 Mod 总开关后按 F3 仍能开自由相机（改的是游戏相机，
                //  与本 Mod 界面无关，用户会以为是游戏自己出了问题）。
                if (!SR.GateMaster) return;
                if (!SR.ComboKeyDown(_key)) return;
                // 按需求：自由相机**全局有效**（不再限定自由模式）；仍只改游戏相机、且仅游戏进行中
                if (_on != null) _on.Value = !_on.Value;                   // **F3 = 快捷勾选那个按钮**
            } catch (Exception __ex) { SR.Guard.Log("自由相机 快捷键", __ex); }
        }
    
        // 游戏实际的渲染相机（照原源码）：ZoomCamera.useCamera -> 其 Camera 组件 -> 静态 CurrentZoomCamera -> Camera.main
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
            } catch (Exception __ex) { SR.Guard.Log("获取当前 ZoomCamera", __ex); }
            return Camera.main;
        }
        public static float CurrentFov() {
            try { Camera cam = GameCamera(); if (cam == null) return _fov != null ? _fov.Value : 10f; return cam.orthographic ? cam.orthographicSize : cam.fieldOfView; } catch { return 10f; }
        }
        // **滚轮缩放（照原源码的灵敏度）**
        public static bool WheelZoom(float wheel) {
            try {
                // **同 CheckKey**：总开关关闭时不消费滚轮（否则会和其它功能的滚轮抢同一事件）。
                if (!SR.GateMaster) return false;
                if (_on == null || !_on.Value || Mathf.Abs(wheel) < 0.001f) return false;
                bool inGame = false;
                try { inGame = (UnityEngine.Cursor.lockState != CursorLockMode.None) || !UnityEngine.Cursor.visible; } catch { inGame = true; }
                if (!inGame) return false;                       //菜单里不抢滚轮
                Camera cam = GameCamera(); if (cam == null) return false;
                // 两分支统一用 ZoomSensitivity（原来 orthographic 硬编码 5f，与 perspective 的 7f 不一致）
                if (cam.orthographic) _fov.Value = Mathf.Clamp(cam.orthographicSize - wheel * ZoomSensitivity, MinFov, MaxFov);
                else _fov.Value = Mathf.Clamp(cam.fieldOfView - wheel * ZoomSensitivity, MinFov, MaxFov);
                return true;
            } catch (Exception __ex) { SR.Guard.Log("自由相机 滚轮", __ex); return false; }
        }
        // **每帧幂等**：锁定时相机 FOV 跟随滑条值（照原源码；仅在自由模式）
        private static float _savedFov = -1f;   // **开启时的原视野（菜单场景/关闭时还原用）**
        // 菜单异常真因：核心经由 Camera.onPreCull 对**每个相机**回调本方法（包括菜单/UI 相机），
        //   而照参考实现会把 FOV 写进**传进来的任何相机** -> 菜单相机被改 -> 主菜单/房间/道具菜单异常。
        //   这里三道闸：1) 开关开 2) 只改**游戏相机**（cam == GameCamera()）3) 只在**游戏进行中**（光标被锁/隐藏）。
        //   不满足时：如果之前改过，把该相机还原，确保菜单渲染正常。
        // 派对盒视野被拉特别远的原因：每帧覆盖 FOV，会**压掉游戏自己的取景**（开箱/派对盒/结算等）。
        //   改为**事件驱动**：只有滚轮/滑块/开启时各写一次；其余时间完全不碰相机。
        //每帧写（照 Camera.cs；只写一次会被控制器拉回）。
        //三闸：1) 关闭时**显式还原**（修：主菜单里关掉后不还原）2) 只认游戏相机（UI/菜单相机不碰）3) 仅游戏进行中。
        public static void ApplyToCamera(Camera cam) {
            try {
                if (cam == null) return;
                Camera g = GameCamera();
                if (g == null || cam != g) return;
                //1) 关闭 / 总开关关闭 / 还没开：把保存的原视野写回去，然后什么都不做
                // **总开关必须在这里判**：本方法是 RegisterCameraView 回调，而核心的
                //    ApplyCameraViews 分发**不带门控**（由功能自己判断）, 缺了它，关掉总开关后
                //    相机仍停在 Mod 改过的 FOV 上（菜单/UI 相机也一并被卷进去）。
                if (!SR.GateMaster || _on == null || !_on.Value) {
                    RestoreFov(cam);
                    return;
                }
                //3) 仅游戏进行中（菜单里也还原）
                bool inGame = false;
                try { inGame = (UnityEngine.Cursor.lockState != CursorLockMode.None) || !UnityEngine.Cursor.visible; } catch { inGame = true; }
                // 原实现这个分支还原后**没有清 _savedFov**（与上一个分支不一致）-> 之后每次
                //  进菜单都会把同一个旧值再写一遍；若游戏自己在菜单动画里改过 FOV，会被反复覆盖回去。
                if (!inGame) { RestoreFov(cam); return; }
                if (cam.orthographic) cam.orthographicSize = Mathf.Clamp(_fov.Value, MinFov, MaxFov);
                else cam.fieldOfView = Mathf.Clamp(_fov.Value, MinFov, MaxFov);
            } catch { }
        }
        //还原"开启时抓到的原视野"并清掉标记（两个早退分支共用，保证行为完全一致）
        private static void RestoreFov(Camera cam) {
            if (_savedFov <= 0f || cam == null) return;
            try { if (cam.orthographic) cam.orthographicSize = _savedFov; else cam.fieldOfView = _savedFov; } catch { }
            _savedFov = -1f;
        }
    }
}
