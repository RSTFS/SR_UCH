// 实验 · 关卡边界页：页面渲染 + 边界解除本体 + 解除 UI3 覆盖。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
[SrFeature("sr.experiments", "Experiments", "1.0")]
public partial class Experiments : ITweak
	{
		//进阶条目行：标签在左、控件紧贴其右（全项目复选框统一排版，见 SR 设置页.RenderEntryRow）。
		private static void AdvRow(string key, string zh, string en, string zhTip, string enTip) {
			try {
				ConfigEntryBase e = SRApi.Ctl.FindEntry("Experiments", key);
				if (e == null) return;
				GUILayout.BeginHorizontal();
				SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T(zh, en), SRApi.T(zhTip, enTip)), e, SRApi.Ctl.Sc(200), SRApi.Ctl.Sc(26));
				GUILayout.Space(SRApi.Ctl.Sc(6));
				SRApi.Ctl.RenderControl(e);
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			} catch (Exception __ex) { SRApi.Guard.Log("渲染进阶条目", __ex); }
		}

		// 条目行统一排版（全项目约定）：标签在左、控件紧贴其右，其余空间留白。
		private static void Row(string zh, string en, string zhTip, string enTip, string key) {
			try {
				ConfigEntryBase e = SRApi.Ctl.FindEntry("Experiments", key);
				if (e == null) return;
				GUILayout.BeginHorizontal();
				SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T(zh, en), SRApi.T(zhTip, enTip)), e, SRApi.Ctl.Sc(200), SRApi.Ctl.Sc(26));
				GUILayout.Space(SRApi.Ctl.Sc(6));
				SRApi.Ctl.RenderControl(e);
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			} catch (Exception __ex) { SRApi.Guard.Log("渲染关卡边界条目", __ex); }
		}

		public static void Render() {
			try {
				SRApi.Ctl.SectionTitle(SRApi.T("- 解除关卡边界 -", "- Unlock level bounds -"));
				// 子项已全部并入主开关（不再单独成键）；旧键由 SR.ConfigMigration 统一清理。
				Row("解除关卡边界", "Unlock level bounds",
					"解除关卡边界：放开关卡四周的空间限制.\n"
					+ "开启后实体墙、放置光标、相机取景、界外黑幕与方块越界放置同时解除; 关闭即逐项还原.",
					"unlocks the level's spatial bounds\n"
					+ "removes the solid walls, placement-cursor bounds, camera framing, the out-of-bounds overlay and out-of-bounds placement at once; turning it off restores each one.",
					"Level Bounds");
				GUILayout.Space(SRApi.Ctl.Sc(6));
				SRApi.Ctl.SectionTitle(SRApi.T("- 进阶（一般不用改）-", "- Advanced (rarely needed) -"));
				// 提示只写默认值，不写范围（AcceptableValueRange 已删除）。
				AdvRow("Level Bounds Radius", "越界范围", "Unlock radius",
					"硬解除半径：光标与相机可到关卡外的距离, 不限范围.\n"
					+ "值越大可走得越远; 相机取景与放置光标的边界都按这个值换掉.",
					"hard unlock radius: how far the cursor and camera can reach outside the level, no upper limit\n"
					+ "larger values reach farther; both the camera framing and the placement-cursor bounds use this value.");
				GUILayout.Space(SRApi.Ctl.Sc(6));
			} catch (Exception __ex) { SRApi.Guard.Log("实验区一 渲染", __ex); }
		}
}
	// 关卡边界（实验）：解除制作关卡时的空间边界限制，与满度上限无关。
	// 四条路：实体墙 size 归零 / 光标边界换 Huge / 相机取景边界 / 方块 IgnoreBounds；改动均记录原值，可逐项还原。
	[SrFeature("sr.levelboundsunlock", "LevelBoundsUnlock", "1.0")]
	public class LevelBoundsUnlock : ITweak {
		private static ConfigEntry<bool> _on;   // 主开关
		private static ConfigEntry<int> _radius;

		internal static string Status = "（尚未探测：打开上面的开关后约 1 秒出现结果）";
		// 方块越界放置并入主开关：Placeable.IgnoreBounds 是纯 bool，CanPlaceCheck 直接读，不经物理回调。
		// 关闭时一律走 Restore() 逐项还原（按 _colOrig/_fixWas/_igWas/_curOrig 等记录的原值写回）。
		private static bool WallsOn { get { return _on != null && _on.Value; } }
		private static bool CursorOn { get { return _on != null && _on.Value; } }
		private static bool CameraOn { get { return _on != null && _on.Value; } }

		internal static bool PieceIgnoreOn {
			get { return _on != null && _on.Value; }
		}

		// 供 RemoveUI3 判断主开关；判据含 SRApi.GateMaster，与 Reeval/Tick 一致。
		internal static bool MasterOn { get { return _on != null && _on.Value && SRApi.GateMaster; } }

		// 原值记录：关闭开关时逐项还原（换场景则整体作废，见 Reset）
		private static readonly Dictionary<Collider2D, Vector2> _colOrig = new Dictionary<Collider2D, Vector2>();
		private static readonly Dictionary<MonoBehaviour, bool> _fixWas = new Dictionary<MonoBehaviour, bool>();
		// _fixWas 兼作"被动过的相机控制器"清单：关闭时不按原值还原 enabled，
		// 而是清 isCameraConfigured 并重新启用，让游戏组件自己重新接管相机。
		// 光标边界原值：只记 boundary（Bounds），不使用 SetBounds(Collider2D) 重载。
		private static readonly Dictionary<PiecePlacementCursor, Bounds> _curOrig = new Dictionary<PiecePlacementCursor, Bounds>();
		private static readonly Dictionary<Placeable, bool> _igWas = new Dictionary<Placeable, bool>();
		private static readonly HashSet<Placeable> _aliveBuf = new HashSet<Placeable>();
		private static readonly List<Placeable> _deadBuf = new List<Placeable>();
		private static System.Reflection.FieldInfo _fiBoundary;
		private static System.Reflection.FieldInfo _fiFrame;
		private static bool _fiTried;
		private static string _hardNote;
		private static int _hardNoteR = int.MinValue;
		private static int _hardNoteN = -1;
		private static string _hardNoteCam;
		// _zcOrig/_zcHas 为相机边界快照，供 Restore 还原。
		private static Bounds _zcOrig = new Bounds(Vector3.zero, Vector3.zero);
		private static bool _zcHas;
		// ZoomCamera 原本的 enabled（Restore 时按原状还原）。
		private static bool _zcWasEnabled;
		// 相机是否被改过：未改过时 Restore 不得动 zc.enabled。
		private static bool _zcTouched;
		private static int _tick;
		private static bool _camDiagDone;
		private static bool _applied;

		// 缓存：Level / 光标 / 相机 / FixCamera / 墙引用
		private static readonly global::Level[] _noLevels = new global::Level[0];
		private static global::Level[] _lvCache = null;
		private static int _lvCacheAt = -9999;
		// 墙扫描的节流时钟必须独立于 Levels 的时钟：共用会让 FindObjectsOfType<Level>() 缓存永久失效。
		private static int _wallScanAt = -9999;
		private static readonly PiecePlacementCursor[] _noCursors = new PiecePlacementCursor[0];
		private static PiecePlacementCursor[] _cursors = _noCursors;
		private static int _cursorsAt = -9999;
		private static ZoomCamera _zc;
		private static int _zcAt = -9999;
		private static FixCameraToBoundaries[] _fixers = new FixCameraToBoundaries[0];
		private static int _fixersAt = -9999;
		// 三面实体墙的碰撞体引用：只在缺失时扫描。
		private static readonly BoxCollider2D[] _wallCol = new BoxCollider2D[3];

		// BlankLevel 场景（level4）中三面实体墙的对象名。
		private static readonly string[] SolidWallNames = { "TopBoundary", "LeftBoundary", "RightBoundary" };
		// 三面墙是 BoxCollider2D，size=(1,1) 靠 transform.scale 撑开 -> size 置 Vector2.zero 即解除；放大无效且会连同精灵变形。
		// 判据为「BLANKLEVEL + 四边齐全」，不判游戏模式。界外的黑来自无背景时的相机清除色，与相机无关。
		private static void SelfReg() {
			// 两处反射均需登记，否则改名后静默返回 null（表现为解除边界无效）。
			SRApi.Ref.Override("Cursor.boundary", "解除关卡边界：放置光标边界快照");
			SRApi.Ref.Override("ZoomCamera.frame", "解除关卡边界：相机取景框");
			SRApi.LocKey("Experiments", "Level Bounds", "解除关卡边界", null);
			SRApi.LocDesc("Experiments", "Level Bounds", "解除关卡边界：放开关卡四周的空间限制\n开启后实体墙、放置光标、相机取景、界外黑幕与方块越界放置同时解除；关闭即逐项还原\n只影响本地，不修改关卡存档与联机同步状态", "unlocks the level's spatial bounds\nremoves the solid walls, placement-cursor bounds, camera framing, the out-of-bounds overlay and out-of-bounds placement at once; turning it off restores each one\nlocal only; does not modify the level save or network sync state");
			SRApi.LocKey("Experiments", "Level Bounds Radius", "硬解除半径", "Hard radius");
			SRApi.LocDesc("Experiments", "Level Bounds Radius", "硬解除半径：光标与相机可到关卡外的距离\n值越大可走得越远，不限范围；填 1 即贴着光标\n仅在主开关开启时生效", "hard unlock radius: how far the cursor and camera can reach outside the level\nlarger values reach farther with no upper limit; 1 keeps it right at the cursor\nonly applies while the main switch is on");
		}

		public void Initialize(IFeatureHost plugin) {
			try {
				SelfReg();
				_on = plugin.Config.Bind("Experiments", "Level Bounds", false,
					"解除关卡边界：实体墙 + 相机取景 + 光标范围 + 方块越界（与满度上限无关）。");
				// 半径不带 AcceptableValueRange，取值不限范围。
				_radius = plugin.Config.Bind("Experiments", "Level Bounds Radius", DefaultHardRadiusInt,
					"硬解除能走多远. 不限范围, 填 1 就是贴着光标走.");
				// 一次性迁移仍为历史默认值的旧配置（改过的不动）；标记放 [Internal] 段，不渲染也不被当孤儿键清掉。
				ConfigEntry<bool> lbDefaults = plugin.Config.Bind("Internal", "Level Bounds Defaults v3", false,
					"内部：硬解除半径的默认值已迁移（200）");
				if (!lbDefaults.Value) {
					try {
						if (_radius.Value == 100000 || _radius.Value == 320 || _radius.Value == 10) _radius.Value = DefaultHardRadiusInt;
						lbDefaults.Value = true;
					} catch (Exception __ex) { SRApi.Guard.Log("边界默认值迁移", __ex); }
				}

				// 事件驱动即时重评估，Tick 仅作兜底（面板关闭/页面切走时 Tick 可能不跑）。
				if (_on != null) _on.SettingChanged += (s, e) => Reeval();
				// 半径变更走轻量刷新（只重跑 ApplyHardBounds，幂等），不触发 Reeval 的全流程重扫。
				if (_radius != null) _radius.SettingChanged += (s, e) => { if (_on != null && _on.Value) { try { ApplyHardBounds(); } catch (Exception __ex) { SRApi.Guard.Log("边界半径刷新", __ex); } } };
				// 半径不挂 SettingChanged，Tick 每帧会调 ApplyHardBounds，改完下一帧自然生效。
				// 旧的子项键已无任何 Config.Bind，统一由 SR.ConfigMigration 的 RemoveKeys 清理。
				SRApi.RegisterTick("关卡边界-应用", Tick);
				SRApi.RegisterSceneHook(Reset);
				SRApi.RegisterDismissHook(Dismiss);
				// 总开关关闭时核心触发 RegisterDismissHook -> Dismiss() -> Restore()。
			} catch (Exception __ex) { SRApi.Guard.Log("关卡边界 初始化", __ex); }
		}

		// 事件驱动的重新评估：开则应用、关则还原；幂等，不依赖 Tick。
		private static void Reeval() {
			try {
				if (_on == null) return;
				bool on = _on.Value && SRApi.GateMaster;
				if (on) {
					_applied = true;
					if (WallsOn) EnsureWalls();
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

		// 每帧总入口：所有分支双向幂等（开写目标值、关写回原值），可任意次切换。
		internal static void Tick() {
			try {
				if (_on == null) return;
				bool on = _on.Value && SRApi.GateMaster;
				ApplyCursorPiece(on);
				if (!on) { if (_applied) { Restore(); _applied = false; } return; }
				_applied = true;
				_tick++;
				// 墙引用缺失时扫描一次。
				if (WallsOn) EnsureWalls();
				// 三面实体墙：size 置 0 = 碰撞面消失，这是能走出原关卡的前提；双向，关闭时写回原值。
				// 此调用必须保持生效。
				OpenSolidWalls(WallsOn);
				// 光标/相机的边界快照，各自看自己的子开关。
				ApplyHardBounds();
				// 相机项开启时才压制 FixCameraToBoundaries（其 Update 会关闭 ZoomCamera 并按 boundaryCollider 重算）。
				SuppressFixCamera(CameraOn);
				if (_tick % 30 != 0) return;
				ApplyAllPiecesIgnore();
				BuildStatus();
			} catch (Exception __ex) { SRApi.Guard.Log("关卡边界 Tick", __ex); }
		}

		// 三面实体墙：只在引用缺失时扫场景；两道闸限制扫描次数 —— 换场景清零、单场景累计上限 WallScanMaxTries 后放弃。
		private static int _wallScanTries;
		private static int _wallScanScene = -1;
		private const int WallScanMaxTries = 60;

		private static void EnsureWalls() {
			bool miss = false;
			for (int i = 0; i < 3; i++) if (_wallCol[i] == null) { miss = true; break; }
			if (!miss) return;
			// 换场景重新计数（墙对象会重建）。
			int sceneHandle = -1;
			try { sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { sceneHandle = -1; }
			if (sceneHandle != _wallScanScene) { _wallScanScene = sceneHandle; _wallScanTries = 0; }
			if (_wallScanTries >= WallScanMaxTries) return;   //本场景已放弃：它没有这三面墙
			if (Time.frameCount - _wallScanAt < 5) return;   //独立时钟，5 帧节流
			_wallScanAt = Time.frameCount;
			_wallScanTries++;
			bool any = false;
			try {
				GameObject[] all = UnityEngine.Object.FindObjectsOfType<GameObject>();
				for (int i = 0; i < all.Length; i++) {
					GameObject g = all[i];
					if (g == null) continue;
					for (int k = 0; k < SolidWallNames.Length; k++) {
						if (_wallCol[k] != null) continue;
						if (g.name == SolidWallNames[k]) {
							BoxCollider2D bc = g.GetComponent<BoxCollider2D>();
							if (bc != null) { _wallCol[k] = bc; any = true; }
						}
					}
				}
			} catch { }
			if (!any) { /* 扫不到留待下次：场景可能未加载完，或本关卡没有这三面墙 */ }
		}

		// 双向：want=true 置零、false 写回原值；原值记在 _colOrig，Restore 会整体清空。
		private static void OpenSolidWalls(bool want) {
			try {
				// 改 collider.size 后必须 SyncPhysics()：宽相不重建会让 CheckColliding 的 InBounds 被打回 false 而放不下。
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

		// 相机取景集合不干预：界外的黑来自无背景时的相机清除色，属背景问题。
		// CamNote() 为只读诊断，把取景框/相机/光标坐标打到状态行，不改任何相机状态。

		// 挂在「相机」子开关上（默认关）：关闭时完全不碰相机，开启时禁用游戏组件并保证 ZoomCamera 开启。
		private static void SuppressFixCamera(bool want) {
			try {
				FixCameraToBoundaries[] fs = Fixers();
				if (!want) {
					// 关闭时不能只还原 enabled：FixCameraToBoundaries 配置完会置 isCameraConfigured 并自关，
					// 须清该字段并强制 enabled=true 让它重新接管相机，再把 ZoomCamera.enabled 按原值还回。
					if (_fixWas.Count == 0 && fs.Length == 0) return;
					int nb = 0;
					// ① 曾记录的控制器（可能已不在当前扫描结果里）
					foreach (KeyValuePair<MonoBehaviour, bool> kv in _fixWas) {
						FixCameraToBoundaries f = kv.Key as FixCameraToBoundaries;
						if (f == null) continue;
						try { f.isCameraConfigured = false; if (!f.enabled) f.enabled = true; nb++; } catch { }
					}
					_fixWas.Clear();
					// ② 当前场景中的控制器
					for (int i = 0; i < fs.Length; i++) {
						FixCameraToBoundaries f = fs[i];
						if (f == null) continue;
						try { f.isCameraConfigured = false; if (!f.enabled) f.enabled = true; nb++; } catch { }
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
				// FixCameraToBoundaries.Update 会关闭 ZoomCamera 并自行接管相机；
				// 禁用该组件后必须重新打开 ZoomCamera，否则相机无人接管（表现为解除边界无效）。
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
				// _igWas 为强引用字典，且游戏销毁方块时不派发事件；以本轮扫描到的集合为权威，剔除已销毁的键。
				// 复用静态缓冲：每 30 帧一次全场景扫描，不再每轮新建 HashSet（满图 500+ 方块时是明显 GC 尖峰）。
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


		// 改运行中 Collider2D 的 size 后同步物理宽相（游戏自身只改序列化编辑器数据，不调 SyncTransforms）。
		// 不改 enabled：那是 Level.EnablePlacementBounds 的职责。
		private static void SyncPhysics() {
			try { Physics2D.SyncTransforms(); }
			catch (Exception __ex) { SRApi.Guard.Log("同步物理宽相", __ex); }
		}

		// 硬解除半径的默认值与兜底值，取值不限范围。
		// 半径必须 > 0：零尺寸 bounds 会让 ZoomCamera 取景框塌成一个点，光标侧的夹取也会被整段跳过。
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
			} catch { }
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
				const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance
					| System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
				for (Type t = typeof(PiecePlacementCursor); t != null; t = t.BaseType) {
					System.Reflection.FieldInfo fi = t.GetField("boundary", BF);
					if (fi != null) { _fiBoundary = fi; break; }
				}
			} catch { }
			return _fiBoundary;
		}
		// ZoomCamera.frame 是 protected 字段，即相机实际取景框。
		private static System.Reflection.FieldInfo FrameField() {
			if (_fiFrame != null) return _fiFrame;
			try {
				const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance
					| System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
				_fiFrame = typeof(ZoomCamera).GetField("frame", BF);
			} catch { }
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

		// 写回已记录的光标原值：光标子开关关闭与功能关闭两条路径共用，避免原值残留。
		private static void RestoreCursorBounds(string why) {
			if (_curOrig.Count == 0) return;
			int nb = 0;
			foreach (KeyValuePair<PiecePlacementCursor, Bounds> kv in _curOrig) {
				if (kv.Key == null) continue;
				try { kv.Key.SetBounds(kv.Value); nb++; } catch { }
			}
			_curOrig.Clear();
			if (nb > 0) SRApi.LogInfo("[关卡边界] " + why + "，已还原 " + nb + " 个光标");
		}

		private static void ApplyHardBounds() {
			try {
				if (_on == null || !_on.Value) return;
				float r = HardRadius();
				int n = 0;
				if (CursorOn) {
					PiecePlacementCursor[] curs = Cursors();
					for (int i = 0; i < curs.Length; i++) {
						PiecePlacementCursor c = curs[i];
						if (c == null) continue;
						Bounds cb;
						if (ReadCursorBounds(c, out cb) && !_curOrig.ContainsKey(c)) _curOrig[c] = cb;
						// 只用 SetBounds(Bounds)，它只写 boundary 字段；
						// SetBounds(Collider2D) 会改写 boundingCollider（即 CheckColliding.CheckBounds 的来源），改错后再也放不下。
						try { c.SetBounds(Huge(c.transform.position)); n++; } catch { }
					}
				} else {
					// 子开关关闭：立刻写回原值。
					RestoreCursorBounds("光标越界关闭");
				}
				string cam;
				{
					// 相机取景边界写 Huge(LevelCenter())：派对盒阶段 targets/boxes 为空，取景框直接取 boundary，会被拉爆；
					// 故整段挂在「相机」子开关上，关闭时不写相机一个字节。
					if (CameraOn) {
						ZoomCamera zc = Zc();
						if (zc != null) {
							if (!_zcHas) { _zcOrig = zc.GetBounds(); _zcHas = true; }
							zc.SetBounds(Huge(LevelCenter()));   // 半径取自 HardRadius()
							cam = "相机✓";
						} else cam = "无相机";
					} else {
						if (_zcHas) {
							try { ZoomCamera z0 = Zc(); if (z0 != null) z0.SetBounds(_zcOrig); } catch { }
							_zcHas = false;
						}
						cam = "相机=不动";
					}
				}
				// 每帧调用：直接比较三个值，避免每帧拼字符串（脏标记只在变化时重建文案）。
			if ((int)r != _hardNoteR || n != _hardNoteN || !string.Equals(cam, _hardNoteCam, StringComparison.Ordinal)) {
				_hardNoteR = (int)r; _hardNoteN = n; _hardNoteCam = cam;
				_hardNote = "硬解除 r=" + (int)r + " 光标×" + n + " " + cam;
			}
			} catch (Exception __ex) { _hardNote = "硬解除失败:" + __ex.Message; SRApi.Guard.Log("关卡边界-硬解除", __ex); }
		}

		// 缓存
		private const int CursorRefreshEvery = 30;
		private static PiecePlacementCursor[] Cursors() {
			int f = Time.frameCount;
			if (f - _cursorsAt < CursorRefreshEvery) return _cursors;
			_cursorsAt = f;
			try {
				PiecePlacementCursor[] a = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
				if (a != null) _cursors = a;
			} catch { }
			return _cursors;
		}
		private static global::Level[] Levels() {
			int f = Time.frameCount;
			if (f - _lvCacheAt < 30 && _lvCache != null) return _lvCache;
			_lvCacheAt = f;
			try {
				global::Level[] a = UnityEngine.Object.FindObjectsOfType<global::Level>();
				if (a != null) _lvCache = a;
			} catch { }
			return _lvCache != null ? _lvCache : _noLevels;
		}
		private static global::Level FirstLevel(global::Level[] lvs) {
			if (lvs == null) return null;
			for (int i = 0; i < lvs.Length; i++) if (lvs[i] != null) return lvs[i];
			return null;
		}
		private static ZoomCamera Zc() {
			int f = Time.frameCount;
			if (f - _zcAt < 30 && _zc != null) return _zc;
			_zcAt = f;
			try {
				ZoomCamera a = UnityEngine.Object.FindObjectOfType<ZoomCamera>();
				if (a != null) _zc = a;
			} catch { }
			return _zc;
		}
		private static FixCameraToBoundaries[] Fixers() {
			int f = Time.frameCount;
			if (f - _fixersAt < 30) return _fixers;
			_fixersAt = f;
			try {
				FixCameraToBoundaries[] a = UnityEngine.Object.FindObjectsOfType<FixCameraToBoundaries>();
				if (a != null) _fixers = a;
			} catch { }
			return _fixers;
		}
		private static Vector3 LevelCenter() {
			global::Level lv = FirstLevel(Levels());
			if (lv == null) return Vector3.zero;
			try { if (lv.CameraBounds != null) return lv.CameraBounds.bounds.center; } catch { }
			try { return lv.GetCameraBounds().center; } catch { }
			return Vector3.zero;
		}

		// 状态行：逐条列出生效项 + 相机取景诊断。
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

		// 相机取景诊断：输出取景框尺寸、相机位置、光标坐标与边界快照尺寸。
		private static string CamNote() {
			try {
				ZoomCamera zc = Zc();
				if (zc == null) return "相机=无";
				Bounds fr = default(Bounds);
				System.Reflection.FieldInfo fi = FrameField();
				if (fi != null) { try { object v = fi.GetValue(zc); if (v != null) fr = (Bounds)v; } catch { } }
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

		// 还原：把改过的每一项按原值写回。
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
				// 改过 collider.size 后同步物理宽相，让 trigger 关系立即重建，否则放置校验会判"放不下"。
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
						// 一律 SetBounds(Bounds)，只还原 boundary。
						// SetBounds(Collider2D) 会改写 boundingCollider（全库无复位代码），改错后只有切场景能恢复。
						kv.Key.SetBounds(kv.Value);
						ncur++;
					} catch { }
				}
				_curOrig.Clear();
				rb.Add("光标原值×" + ncur);
				ZoomCamera zc = Zc();
				if (zc != null && _zcHas) { try { zc.SetBounds(_zcOrig); rb.Add("相机=原快照"); } catch { } }
				_zcHas = false;
				// 仅在相机项开过（_zcTouched）且原值本为关闭时把 ZoomCamera.enabled 关回，其余情况不动。
				if (_zcTouched) { try { ZoomCamera zr = Zc(); if (zr != null && !_zcWasEnabled && zr.enabled) zr.enabled = false; } catch { } }
				_zcTouched = false;
				Status = "（已还原为游戏原值）" + string.Join(" ", rb.ToArray());
			} catch (Exception __ex) { Status = "（还原异常：" + __ex.Message + "）已还原：" + string.Join(" ", rb.ToArray()); SRApi.Guard.Log("关卡边界 还原", __ex); }
			_hardNote = null;
			// 与 Reset 对齐：清除"只告警一次"的标志，关闭后再开启仍能看到诊断。
			_camDiagDone = false;
		}

		// 换场景：Level / 相机 / 光标 / 方块均为新对象，旧原值字典整体作废。
		private static void Reset() {
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
		}

		private static void Dismiss() {
			try { if (_applied) { Restore(); _applied = false; } }
			catch (Exception __ex) { SRApi.Guard.Log("关卡边界 收起", __ex); }
		}
	}
	// 解除 UI 3 覆盖：BlankLevel 里 4 个纯黑 whiteSquare 位于 UI 3 层并盖住界外，停画它们及该层所有 Renderer。
	[SrFeature("sr.removeui3", "RemoveUI3", "1.0")]
	public class RemoveUI3 : ITweak {
		private static readonly Dictionary<Renderer, bool> _wasOn = new Dictionary<Renderer, bool>();
		internal static string Note = "";
		private static int _frame = -9999;
		private static int _hitCount = -1;
		private static int _ui3ScanTries;                     // 本场景已扫描次数（封顶后放弃）
		private const int Ui3ScanMaxTries = 6;                 // 2.5s × 6 ≈ 15 秒
		private static float _nextScanAt;    // 下次全扫时间
		private static string _scanScene;    // 上次扫描的场景名（换场景立即重扫）

		public void Initialize(IFeatureHost plugin) {
			try {
				SRApi.RegisterTick("解除UI3", Tick);
				SRApi.RegisterSceneHook(Reset);
				SRApi.RegisterDismissHook(Restore);
			} catch (Exception __ex) { SRApi.Guard.Log("解除UI3 初始化", __ex); }
		}

		// 换场景必须清 _wasOn：其为强引用字典，旧 Renderer 已随场景销毁，原值无意义且会逐场累积。
		private static void Reset() { _hitCount = -1; _scanScene = null; _nextScanAt = 0f; _ui3ScanTries = 0; Note = ""; _wasOn.Clear(); }

		private static void Tick() {
			try {
				if (_frame == Time.frameCount) return;
				_frame = Time.frameCount;
				// 已并入「解除关卡边界」主开关。
				bool want = LevelBoundsUnlock.MasterOn;
				if (!want) { Restore(); return; }
				if (!SRApi.GateMaster) { Restore(); return; }
				Apply();
			} catch (Exception __ex) { SRApi.Guard.Log("解除UI3 Tick", __ex); }
		}

		private static void Apply() {
			try {
				// 扫描时机：首次 / 换场景立即全扫，之后按 unscaled 时间每 2.5 秒复查一次（覆盖加载后才生成的对象）。
				string sc = "";
				try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
				float now = Time.unscaledTime;
				// 次数上限：换场景清零，本场景最多扫 Ui3ScanMaxTries 次（≈15 秒）后放弃，避免无限期全场景扫描。
				if (sc != _scanScene) { _scanScene = sc; _ui3ScanTries = 0; }
				if (_hitCount >= 0 && now < _nextScanAt) return;
				if (_ui3ScanTries >= Ui3ScanMaxTries) return;
				_ui3ScanTries++;
				_nextScanAt = now + 2.5f;
				Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
				if (all == null) return;
				int n = 0;
				for (int i = 0; i < all.Length; i++) {
					Renderer r = all[i];
					if (r == null) continue;
					string ln;
					try { ln = r.sortingLayerName; } catch { continue; }
					if (!IsUI3(ln)) continue;
					if (!_wasOn.ContainsKey(r)) _wasOn[r] = r.enabled;
					if (r.enabled) r.enabled = false;
					n++;
				}
				_hitCount = n;
				Note = n > 0 ? ("已解除 UI3 覆盖：停画 " + n + " 个 Renderer")
							 : "未找到 UI3 层上的 Renderer（本关卡可能没有）";
			} catch (Exception __ex) { SRApi.Guard.Log("应用UI3解除", __ex); }
		}

		// UI 3 的显示名可能带空格，sortingLayerName 为配置中的原样字符串。
		private static bool IsUI3(string layerName) {
			if (string.IsNullOrEmpty(layerName)) return false;
			return layerName == "UI 3" || layerName == "UI3";
		}

		private static void Restore() {
			try {
				foreach (KeyValuePair<Renderer, bool> kv in _wasOn) {
					if (kv.Key == null) continue;
					try { kv.Key.enabled = kv.Value; } catch { }
				}
				_wasOn.Clear();
			} catch { }
			_hitCount = -1;
			Note = "";
		}
	}
}
