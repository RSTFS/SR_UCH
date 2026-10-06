// 实验 · 关卡边界页。页面渲染 + 边界解除本体 + 解除 UI3 覆盖（就是那层黑方块）。
// 这条路踩过的坑特别多，取证过程和结论都写在 LevelBoundsUnlock 的注释里，动它之前建议先读一遍。
// 整个实验栏目的总览看同目录的 Experiments.cs。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
	{
		//（原警告样式缓存已删：grep 确认 WarnStyle() 全项目零调用, 是 IMGUI 样式统一上收到
		//  SR.Ctl 之后留下的死代码；需要告警配色时直接用 SR.Ctl 的现成样式即可。）
		//进阶条目行：标签在左、控件紧贴其右（ 全项目复选框统一排版，见 SR.Settings.RenderEntryRow 说明）。
		private static void AdvRow(string key, string zh, string en, string zhTip, string enTip) {
			try {
				ConfigEntryBase e = SR.Ctl.FindEntry("Experiments", key);
				if (e == null) return;
				GUILayout.BeginHorizontal();
				SR.Ctl.RestoreLabel(new GUIContent(SR.T(zh, en), SR.T(zhTip, enTip)), e, SR.Ctl.Sc(200), SR.Ctl.Sc(26));
				GUILayout.Space(SR.Ctl.Sc(6));
				SR.Ctl.RenderControl(e);
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			} catch (Exception __ex) { SR.Guard.Log("渲染进阶条目", __ex); }
		}

		// 条目行统一排版（全项目约定）：标签在左、控件**紧贴其右**，其余空间留白。
		//  原来关卡边界区的每行是标签(宽200)和控件分属两个 GUILayout 行 -> 竖排两行、很松散；
        //  而通用条目行（RenderEntryRow）里控件前有 FlexibleSpace -> 复选框被推到行尾、与标签脱开。
		//  这里统一成标签 + 紧贴控件 + FlexibleSpace。
		private static void Row(string zh, string en, string zhTip, string enTip, string key) {
			try {
				ConfigEntryBase e = SR.Ctl.FindEntry("Experiments", key);
				if (e == null) return;
				GUILayout.BeginHorizontal();
				SR.Ctl.RestoreLabel(new GUIContent(SR.T(zh, en), SR.T(zhTip, enTip)), e, SR.Ctl.Sc(200), SR.Ctl.Sc(26));
				GUILayout.Space(SR.Ctl.Sc(6));
				SR.Ctl.RenderControl(e);
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			} catch (Exception __ex) { SR.Guard.Log("渲染关卡边界条目", __ex); }
		}

		public static void Render() {
        try {
            GUILayout.Label(SR.T("- 解除关卡边界 -", "- Unlock level bounds -"), SR.Ctl.SecHeader);
            Row("解除关卡边界", "Unlock level bounds",
                "解除制作关卡时的空间边界限制：可以往边界外继续放方块、角色也能走出原关卡范围。\n"
              + " 与建造增强 -> 解除建造上限不同：那个管满度/数量，这个管范围。",
                "Removes the spatial boundary limit while building a level.", "Level Bounds");
            Row("解除 UI3 覆盖", "Remove UI3 overlay",
                "解决界外一片黑的问题. UI3 层上有几个纯黑方块挡住了视野, 这一项让它们不再显示.",
                "Stops drawing the black squares on the 'UI 3' layer (the real cause of the black void).",
                "Remove UI3");
            Row("方块可越界放置", "Pieces ignore bounds",
                "让方块可以放到关卡边界之外（只影响放置，角色能否走出去看上面那项）。",
                "Allow placing pieces outside the level bounds.", "Level Bounds Piece Ignore");
            GUILayout.Space(SR.Ctl.Sc(6));
            GUILayout.Label(SR.T("- 进阶（一般不用改）-", "- Advanced (rarely needed) -"), SR.Ctl.SecHeader);
            // **R396 N12**：这两条 tip 原来写"默认 200 / 0 = 不外扩"，而 Bind 默认已是 500、
            //  且 AcceptableValueRange(200,1000) **不允许输入 0** -> 那个选项在界面上根本不存在。
            //  现在按代码实际值写：范围 200-1000、默认 500。
            AdvRow("Level Bounds Margin", "边界外扩", "Extra margin",
                "把边界每边向外扩多少. 数值越大, 相机和放置范围看到的区域越大.",
                "Expand the boundary on each side (200-1000, default 500).");
            AdvRow("Level Bounds Radius", "硬解除半径", "Hard radius",
                "硬解除能走多远. 数值越大, 光标和相机就越不受边界限制. 关卡很大时才需要调大.",
                "How far the hard unlock lets you go (100000-1000000, default 500000).");
            GUILayout.Space(SR.Ctl.Sc(6));
        } catch (Exception __ex) { SR.Guard.Log("实验区一 渲染", __ex); }
		}
}
    // 关卡边界（实验）。管的是制作关卡时的**空间边界限制**，不是满度上限（那在建造增强里）。
    // 思路：不拦游戏的校验方法，而是把边界对象本身放大 —— 效果一样，还能完整还原；探不到可写成员就什么都不做。
    // 真实链路（放置判定看 Level.CursorBounds 而不看 ExtraCursorBounds、光标范围走 Level.GetCursorBounds、
    // 官方破法是 Placeable.IgnoreBounds）已收到 REF_game_facts.md 的「关卡边界」一节。
    // 本功能三条路一起走：扩放置碰撞体 + 扩光标范围 + 手上方块 IgnoreBounds 兜底。改动全记原值，可还原。
    public class LevelBoundsUnlock : ITweak {
        private static ConfigEntry<bool> _on;
        private static ConfigEntry<int> _margin;
        private static ConfigEntry<bool> _hard;
        private static ConfigEntry<int> _radius;
        private static ConfigEntry<bool> _pieceIgnore;

        internal static string Status = "（尚未探测：打开上面的开关后约 1 秒出现结果）";
        // **R415**：解除边界生效时，**方块的越界放置一并打开**，不再要求用户额外勾「方块可越界放置」。
        //
        // 顺带修一个真 bug：原来这里是 `_pieceIgnore == null || _pieceIgnore.Value`，
        // 也就是**「没绑定时返回 true」** —— 语义完全反了（没绑定应该是不生效）。
        // 结果「方块可越界放置」这个开关在默认状态下就一直是开的，而界面上它的复选框是关的，
        // 用户看到的现象是"只开了解除边界就放不下、勾上那个更离谱"。
        //
        // 为什么必须把它并进总开关（取证结论）：
        //   放置判定 `CanPlaceCheck` @12966 要求 `flag`（= 有任一 `CheckColliding.InBounds` 为真），
        //   而 `InBounds` 每物理帧被 `CheckColliding.FixedUpdate` @18402 打成 `CheckBounds == null`，
        //   只能靠 `OnTriggerStay2D` @18433 真的碰到 `CheckBounds`（= `Level.CursorBounds`）才置回真。
        //   这条**纯物理**链路在联机下极不可靠（各端各自 Instantiate、broadphase 不同步）。
        //   而 `Placeable.IgnoreBounds` 是**纯 bool**，`CanPlaceCheck` @12899 直接读
        //   `if (item.InBounds || IgnoreBounds)` —— **完全不经过物理回调**，稳。
        //   整个特性的目的就是"能往边界外放方块"，这两件事本来就是一件事。
        internal static bool PieceIgnoreOn {
            get { return _on != null && _on.Value && (_pieceIgnore == null || _pieceIgnore.Value); }
        }

        // 原值记录：关闭开关时逐项还原（换场景则整体作废，见 Reset）
        private static readonly Dictionary<Collider2D, Vector2> _colOrig = new Dictionary<Collider2D, Vector2>();
        private static readonly Dictionary<global::Level, float> _extraOrig = new Dictionary<global::Level, float>();
        private static readonly Dictionary<Transform, Vector3> _edgeOrig = new Dictionary<Transform, Vector3>();
        private static readonly Dictionary<MonoBehaviour, bool> _fixWas = new Dictionary<MonoBehaviour, bool>();
        // 光标边界原值。只记 boundary（Bounds）就够了。
        // **R415**：R413 我在这里加过一个"原 collider"字典，Restore 走 SetBounds(Collider2D) 一起还原
        // boundingCollider —— **那是错的**，详见下面 Restore 处的说明。现在一律只还原 boundary。
        private static readonly Dictionary<PiecePlacementCursor, Bounds> _curOrig = new Dictionary<PiecePlacementCursor, Bounds>();
        private static readonly Dictionary<Placeable, bool> _igWas = new Dictionary<Placeable, bool>();
        private static System.Reflection.FieldInfo _fiBoundary;
        private static System.Reflection.FieldInfo _fiFrame;
        private static bool _fiTried;
        private static string _hardNote;
        private static string _hardNoteKey = "";   // **R396 N1**：_hardNote 的脏标记
        private static string _softNote;
        //**R416-4**：硬解除已不再改ZoomCamera.boundary（那是被拉爆的真凶，见 ApplyHardBounds），
        //  下面这两个字段现在只用于 Restore 的"成对清理"（_zcHas 复位，不再有写入路径）。
        private static Bounds _zcOrig = new Bounds(Vector3.zero, Vector3.zero);
        private static bool _zcHas;
        // **R417**：记下 ZoomCamera 原本的 enabled（Restore 时按原状还原）
        private static bool _zcWasEnabled;
        private static int _tick;
        private static bool _camDiagDone;
        private static bool _applied;

        // **缓存**：Level / 光标 / 相机 / FixCamera / 墙引用
        private static readonly global::Level[] _noLevels = new global::Level[0];
        private static global::Level[] _lvCache = null;
        private static int _lvCacheAt = -9999;
        // R396 S1：墙扫描的节流时钟**必须独立**。
        //  原来 EnsureWalls(5 帧节流) 与 Levels(30 帧节流) 共用 _lvCacheAt，
        //  而 Tick 的顺序是 EnsureWalls -> ... -> ApplyLevelGeometry -> Levels：
        //  非自定义关卡/场景未加载完时三面墙永远找不到 -> EnsureWalls 每 5 帧把时间戳刷成当前帧
        //  -> Levels() 读到的差恒为 0 -> FindObjectsOfType<Level>() **再也不重跑**（缓存永久 stale）。
        //  症状就是"改了边界值要过一会儿甚至换场景才生效"。
        private static int _wallScanAt = -9999;
        private static readonly PiecePlacementCursor[] _noCursors = new PiecePlacementCursor[0];
        private static PiecePlacementCursor[] _cursors = _noCursors;
        private static int _cursorsAt = -9999;
        private static ZoomCamera _zc;
        private static int _zcAt = -9999;
        private static FixCameraToBoundaries[] _fixers = new FixCameraToBoundaries[0];
        private static int _fixersAt = -9999;
        // 三面实体墙的碰撞体引用：**只在缺失时扫一次**（R382 修掉帧的主因）
        private static readonly BoxCollider2D[] _wallCol = new BoxCollider2D[3];

        // BlankLevel 场景（level4）里真实存在的三面**实体墙**的名字, 解除它们才真的能走出去。
        private static readonly string[] SolidWallNames = { "TopBoundary", "LeftBoundary", "RightBoundary" };
        // 这条链路的完整取证结论已收到 REF_game_facts.md 的「关卡边界」一节（三面实体墙 / 四个 LevelBoundTarget 把相机围死 / 已排除的方向），这里只留要点：
        // · 三面墙是 BoxCollider2D，size=(1,1) 靠 transform 的 scale 撑开 -> 解法是 size 置 Vector2.zero。
        // **别放大**：那只是把墙推远，走更远照样撞上；而且 size 会被 scale 乘，改它连精灵一起变形。
        // · 判据必须写「BLANKLEVEL + 四边齐全」，不要判游戏模式（旧实现用 SR.Env.TryGetMode，取不到就整段跳过 = 静默失效）。
        // · 界外那片黑跟相机无关，是没有背景时的相机清除色 -> 属于背景问题（见 LevelTools 的关卡背景）。
        private static void SelfReg() {
            //**V4**：这两处原来是裸反射、没登记 -> 游戏改名后静默返回 null（表现为"解除边界点不动"），
            //  启动自检里也看不出来。登记后 RefAudit 会逐条解析并报告。
            SR.RefOverride("Cursor.boundary", "解除关卡边界：放置光标边界快照");
            SR.RefOverride("ZoomCamera.frame", "解除关卡边界：相机取景框");
            SR.LocKey("Experiments", "Level Bounds", "解除关卡边界", null);
            SR.LocDesc("Experiments", "Level Bounds",
                "把关卡的空间边界解开, 方块能放到外面, 角色也能走出去. 和解除建造上限不一样, 那个管数量, 这个管范围.",
                "Removes the spatial boundary limit while building a level.");
            SR.LocDesc("Experiments", "Level Bounds Piece Ignore",
                "让方块可以放到关卡边界外面. 只影响放置, 不影响角色能不能走出去. 关闭时会恢复原值.",
                "Set Placeable.IgnoreBounds on every piece (restored one by one when disabled).");
            SR.LocKey("Experiments", "Level Bounds Margin", "边界外扩", "Extra margin");
            SR.LocDesc("Experiments", "Level Bounds Margin",
                "每边往外扩多少, 200 到 1000, 默认 500. 自制关卡实测那四个边界点只围出一小块, 不扩的话相机永远只看得到那么点.",
                "Expand the boundary on each side (200-1000, default 500).");
            SR.LocKey("Experiments", "Level Bounds Hard", "硬解除", "Hard unlock");
            SR.LocDesc("Experiments", "Level Bounds Hard",
                "把卡住光标和相机的那两个边界快照直接换成很大范围. 游戏那边是一次性快照, 不会跟着关卡变, 所以只能这样换.",
                "Force the clamp snapshots to a huge range.");
            SR.LocKey("Experiments", "Level Bounds Radius", "硬解除半径", "Hard radius");
            SR.LocDesc("Experiments", "Level Bounds Radius",
                "硬解除能走多远, 10 万到 100 万(原值 10 万的 1 到 10 倍), 默认 10 万. 原版关卡的相机框只有 100 来, 10 万已经远远够用.",
                "How far the hard unlock lets you go: 100000 to 1000000 (1x to 10x the built-in value), default 100000.");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _on = plugin.Config.Bind("Experiments", "Level Bounds", false,
                    "解除关卡边界：实体墙 + 相机取景 + 光标范围 + 方块越界（与满度上限无关）。");
                _margin = plugin.Config.Bind("Experiments", "Level Bounds Margin", 500,
                    new ConfigDescription("边界每边向外扩多少（200-1000）",
                        new AcceptableValueRange<int>(200, 1000)));
                _hard = plugin.Config.Bind("Experiments", "Level Bounds Hard", false,
                    "硬解除: 把夹住光标和相机的边界快照换成硬解除半径那么大的范围.");
                _pieceIgnore = plugin.Config.Bind("Experiments", "Level Bounds Piece Ignore", false,
                    "给所有方块（含手上待放的）置 Placeable.IgnoreBounds，允许越界放置。");
                // **R413-10**：默认改回原值 100000（原来 500000 是拍脑袋定的，5 倍冗余）。
                //  UnityPy 实测原版关卡的相机框只有 ~105x89、自定义关卡 130x90 -> 10 万已经绰绰有余，
                //  调太大正是"相机拉得特别远"的直接原因（取景框被撑大 -> 画面里所有东西小到看不见）。
                _radius = plugin.Config.Bind("Experiments", "Level Bounds Radius", DefaultHardRadiusInt,
                    new ConfigDescription("硬解除能走多远（原值 10 万 ~ 10 倍 = 10 万 ~ 100 万）",
                        new AcceptableValueRange<int>(DefaultHardRadiusInt, DefaultHardRadiusInt * 10)));

        // R387 问题 2关卡边界的所有功能都要能启用后关闭、关闭后再启用、且持久化：
                //  原先只在 Tick 里判 if (!on) Restore(), 那依赖 Tick 每帧都被调用。
                //  只要出现"面板关着/页面切走/Tick 被跳过"的情形，_applied 就一直是 true -> 关不掉。
                //  现在补一层**事件驱动**：任一相关配置项变化时立刻重新评估（开->关、关->开都即时生效），
                //  Tick 只作为兜底（例如关卡加载后自动重应用）。
                if (_on != null) _on.SettingChanged += (s, e) => Reeval();
                if (_hard != null) _hard.SettingChanged += (s, e) => Reeval();
                if (_pieceIgnore != null) _pieceIgnore.SettingChanged += (s, e) => Reeval();
                // **R413-8**：这两个改**轻量刷新**而不是 Reeval()。
                //  Reeval 是"开/关"的全流程（重扫墙 + 重建全部原值快照）；拖动滑块时它跟着每帧重入，
                //  既慢又会让外扩/硬解除在拖动中途反复重写（观感上就是"拖不动、被拉回"）。
                //  这里只重跑受影响的那一步，两步都是幂等的。
                if (_margin != null) _margin.SettingChanged += (s, e) => { if (_on != null && _on.Value) { try { ApplyLevelGeometry(); } catch (Exception __ex) { SR.Guard.Log("边界外扩刷新", __ex); } } };
                // **R414**：半径**不再挂 SettingChanged**。原来拖动中每次提交都同步跑一遍 ApplyHardBounds，
                // 而它要遍历光标 + 写 ZoomCamera 边界；配合"提交窗口被 Layout 趟冲掉"就表现为拖一下弹回。
                // Tick 每帧本来就会调 ApplyHardBounds，配置改完下一帧自然生效，不需要事件驱动。
                // R392：这两项改用**滑块条**渲染（用户要求）
                // **R418-2**：外扩与半径**改回编辑框**（用户要求）。
                //  滑块对这两个条目不合适：Margin 200-1000、Radius 10 万-100 万都是大跨度 int，
                //  轨道上每一像素代表的差值极大，拖动时几乎"拖不动"、也看不出精确值。
                //  原来 R404 之前就是编辑框（配合 AcceptableValueRange + 上下限按钮），
                //  显式登滑块的那一行一并删掉。
                SR.RegisterTick("关卡边界-应用", Tick);
                SR.RegisterSceneHook(Reset);
                SR.RegisterDismissHook(Dismiss);
                // 总开关关闭时核心会触发 RegisterDismissHook -> Dismiss() -> Restore()（已注册）
            } catch (Exception __ex) { SR.Guard.Log("关卡边界 初始化", __ex); }
        }

        // 事件驱动的重新评估（问题 2 的核心）：配置变化 / 总开关变化时立刻"开就应用、关就还原"，
        //  不依赖 Tick 是否被调用。幂等：重复调用无副作用。
        private static void Reeval() {
            try {
                if (_on == null) return;
                bool on = _on.Value && SR.GateMaster;
                if (on) {
                    _applied = true;
                    EnsureWalls();
                    OpenSolidWalls();
                    ApplyHardBounds();
                    SuppressFixCamera();
                    ApplyLevelGeometry();
                    ApplyAllPiecesIgnore();
                    BuildStatus();
                } else {
                    ApplyCursorPiece(false);
                    if (_applied) { Restore(); _applied = false; }
                }
            } catch (Exception __ex) { SR.Guard.Log("关卡边界 重评估", __ex); }
        }

        // 每帧总入口。所有分支都写成**双向幂等**：开时写目标值，关时写回原值，
        //  这样开->关->开->关任意次切换都不会残留上一次的状态。
        internal static void Tick() {
            try {
                if (_on == null) return;
                bool on = _on.Value && SR.GateMaster;
                ApplyCursorPiece(on);
                if (!on) { if (_applied) { Restore(); _applied = false; } return; }
                _applied = true;
                _tick++;
                // 换场景/首次：墙引用缺失才扫一次（ R382 修掉帧的主因）
                EnsureWalls();
                // **三面实体墙**：size 置 0 = 碰撞面消失（这是"角色能真正走出原关卡"的唯一原因）
                //  R391 精简时误删了这个调用 -> 墙还在、突破不了。**任何改动这里都要先确认它在跑。**
                OpenSolidWalls();
                // 每帧：光标/相机的边界快照
                ApplyHardBounds();
                // FixCameraToBoundaries 必须**每帧**压制：它的 Update 会
                //  zoomCamera.enabled=false + 把相机钉在 boundaryCollider 中心 + 永不再重算。
                SuppressFixCamera();
                if (_tick % 10 == 0) ApplyLevelGeometry();
                if (_tick % 30 != 0) return;
                ApplyAllPiecesIgnore();
                BuildStatus();
            } catch (Exception __ex) { SR.Guard.Log("关卡边界 Tick", __ex); }
        }

        // 三面实体墙：只在引用缺失时扫一次场景
        // **P1-07**：扫描次数上限 + 换场景重置。
        //  三面实体墙只存在于**自定义关卡场景**（Data\level4）里；在官方关卡 / 树屋大厅中
        //  _wallCol[] 永远是 null -> miss 恒真 -> 原逻辑会每 5 帧跑一次全场景
        //  FindObjectsOfType<GameObject>()（关卡里对象可达上万），一直掉帧。
        //  这里加两道闸：① 换场景才把计数清零；② 单场景累计扫够 WallScanMaxTries 次就放弃
        //  （60 次 × 每 5 帧一次 = 约 5 秒，足够等场景加载完）。
        private static int _wallScanTries;
        private static int _wallScanScene = -1;
        private const int WallScanMaxTries = 60;

        private static void EnsureWalls() {
            bool miss = false;
            for (int i = 0; i < 3; i++) if (_wallCol[i] == null) { miss = true; break; }
            if (!miss) return;
            //换场景 -> 重新给机会扫（自定义关卡之间来回切换时墙对象会重建）
            int sceneHandle = -1;
            try { sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { sceneHandle = -1; }
            if (sceneHandle != _wallScanScene) { _wallScanScene = sceneHandle; _wallScanTries = 0; }
            if (_wallScanTries >= WallScanMaxTries) return;   //本场景已放弃：它没有这三面墙
            // **只在缺墙时扫**。R382 之前是每 60 帧无条件 FindObjectsOfType<GameObject>()
            //  全场景扫描, 那是开了掉帧的主因。
            if (Time.frameCount - _wallScanAt < 5) return;   //别在同一帧里反复扫（ 独立时钟）
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
            if (!any) { /* 扫不到就下次再试（场景可能还没加载完，或本关卡本来就没有这三面墙） */ }
        }

        private static void OpenSolidWalls() {
            try {
                for (int i = 0; i < 3; i++) {
                    BoxCollider2D bc = _wallCol[i];
                    if (bc == null) continue;
                    Vector2 orig;
                    if (!_colOrig.TryGetValue(bc, out orig)) { orig = bc.size; _colOrig[bc] = orig; }
                    if (bc.size != Vector2.zero) {
                        bc.size = Vector2.zero;
                        bool was = bc.enabled;
                        if (was) { bc.enabled = false; bc.enabled = true; }
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("解除实体墙", __ex); }
        }

        // **R383**：这里原来有相机取景集合的干预（把 Level 的 StartPoint/Goal/GoalBlock 从
        //  ZoomCamera.targets 里摘掉，以解决界外看不到）。**已按用户要求删除**,
        //  黑域的黑色来自相机清除色（游戏内没有任何"按坐标隐藏物体"的机制，
        //  相机的 clearFlags 只在截图相机上设置），那属于**背景**问题，见 LevelTools 的关卡背景；
        //  相机取景不该由本功能插手。保留下面的 CamNote() **只读**诊断：
        //  它不改任何相机状态，只是把取景框/相机/光标坐标打到状态行上，
        //  万一"界外看不见"另有原因，这份数据能直接指认是哪一环。

        // 关卡几何：外移四个 LevelBoundTarget + 放大测量盒 + 加大 ExtraCursorBounds
        private static void ApplyLevelGeometry() {
            try {
                float margin = _margin != null ? (float)_margin.Value : 0f;
                global::Level[] lvs = Levels();
                for (int i = 0; i < lvs.Length; i++) {
                    global::Level lv = lvs[i];
                    if (lv == null) continue;
                    if (margin > 0f && lv.thisLevelis == GameState.LevelName.BLANKLEVEL
                        && lv.Left != null && lv.Right != null && lv.Top != null && lv.Bottom != null) {
                        MoveEdge(lv.Left, new Vector3(-margin, 0f, 0f));
                        MoveEdge(lv.Right, new Vector3(margin, 0f, 0f));
                        MoveEdge(lv.Top, new Vector3(0f, margin, 0f));
                        MoveEdge(lv.Bottom, new Vector3(0f, 0f - margin, 0f));
                    }
                    if (margin > 0f) {
                        if (lv.CursorBounds != null) GrowCollider(lv.CursorBounds, margin);
                        if (lv.CameraBounds != null && !ReferenceEquals(lv.CameraBounds, lv.CursorBounds))
                            GrowCollider(lv.CameraBounds, margin);
                    }
                    float origExtra;
                    if (!_extraOrig.TryGetValue(lv, out origExtra)) { origExtra = lv.ExtraCursorBounds; _extraOrig[lv] = origExtra; }
                    float nv = origExtra + margin;
                    if (lv.ExtraCursorBounds != nv) lv.ExtraCursorBounds = nv;
                }
                _softNote = "四边/测量盒 外扩 " + (int)margin + " 每边";
            } catch (Exception __ex) { SR.Guard.Log("关卡几何", __ex); }
        }

        private static void SuppressFixCamera() {
            try {
                FixCameraToBoundaries[] fs = Fixers();
                for (int i = 0; i < fs.Length; i++) {
                    FixCameraToBoundaries f = fs[i];
                    if (f == null) continue;
                    if (!_fixWas.ContainsKey(f)) _fixWas[f] = f.enabled;
                    if (f.enabled) f.enabled = false;
                }
                // **R417-3（用户报：解除边界无效了 —— 这是 R416 删掉下面那行造成的回归）**
                //
                // R416 我把 `if (!zc.enabled) zc.enabled = true;` 当成"拉爆相机的帮凶"删了，
                // **判断反了**。看 `FixCameraToBoundaries` @123889 完整实现：
                //   Update() @123903: 找到 ZoomCamera -> `zoomCamera.enabled = false` @123916
                //                     -> AdjustCamera()（按自己的 boundaryCollider 算 orthographicSize）
                //                     -> isCameraConfigured = true（@123932，下次 Update 直接自关 @123907）
                // 也就是说：**这个组件一运行就把 ZoomCamera 关掉、自己接管相机**。
                // 我禁用它之后，若不把 ZoomCamera 重新打开，**相机就彻底没人管了** ——
                // 表现为"解除边界无效"（画面不跟着走、边界也没解开）。
                //
                // 与R416 的"相机拉爆"不矛盾：那次爆的根因是**给 `ZoomCamera.boundary` 写Huge**
                // （`GetFrame` @150098只单向收窄、无上限 -> `GetFOV` @150203 反推 FOV）。
                // R416 已经**不再改 boundary** 了，所以现在把 `zc.enabled` 开回来是安全的：
                // 它按游戏自己给的边界（`MainCamera.SetBounds(LevelLayout.GetCameraBounds())` @126550，
                // 而那已经是**我们放大过的**关卡几何）取景，既能解除边界、又不会爆。
                ZoomCamera zc = Zc();
                if (zc != null) {
                    if (!_zcHas) _zcWasEnabled = zc.enabled;   // **R417**：只在首次记录原值
                    if (!zc.enabled) zc.enabled = true;
                }
            } catch (Exception __ex) { SR.Guard.Log("压制 FixCamera", __ex); }
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
            } catch (Exception __ex) { SR.Guard.Log("关卡边界-手上方块", __ex); }
        }

        private static void ApplyAllPiecesIgnore() {
            try {
                bool on = PieceIgnoreOn;
                Placeable[] all = UnityEngine.Object.FindObjectsOfType<Placeable>();
                // R396 S13：_igWas 是 Dictionary<Placeable, bool>（**强引用**）。
                //  游戏自己销毁方块走 destroyMarkedPieces（反编译 125809 Object.Destroy），
                //  **不派发 DestroyPieceEvent** -> 这里收不到通知，而 Unity 伪 null 只让
                //  p == null 成立、字典键仍是同一个托管引用 -> 字典单调增长、每个键钉住一份包装。
                //  一局几百个方块生灭 -> 长时间对局内存单调增长，Restore() 的遍历开销随之线性上升。
                //  做法：本轮扫到的 alive 集合就是权威，扫不到的一律剔除（学 DestroyBlocks.PlayerPlacedBlocks:642）。
                HashSet<Placeable> alive = null;
                if (_igWas.Count > 0) alive = new HashSet<Placeable>();
                for (int i = 0; i < all.Length; i++) {
                    Placeable p = all[i];
                    if (p == null) continue;
                    if (alive != null) alive.Add(p);
                    if (!_igWas.ContainsKey(p)) _igWas[p] = p.IgnoreBounds;
                    if (p.IgnoreBounds != on) p.IgnoreBounds = on;
                }
                if (alive != null) {
                    List<Placeable> dead = null;
                    foreach (Placeable k in _igWas.Keys) {
                        if (alive.Contains(k)) continue;
                        if (dead == null) dead = new List<Placeable>();
                        dead.Add(k);
                    }
                    if (dead != null) for (int i = 0; i < dead.Count; i++) _igWas.Remove(dead[i]);
                }
            } catch (Exception __ex) { SR.Guard.Log("全场方块 IgnoreBounds", __ex); }
        }

        private static void GrowCollider(Collider2D col, float margin) {
            BoxCollider2D bc = col as BoxCollider2D;
            if (bc == null) return;
            Vector2 orig;
            if (!_colOrig.TryGetValue(bc, out orig)) { orig = bc.size; _colOrig[bc] = orig; }
            Vector2 nv = new Vector2(orig.x + margin * 2f, orig.y + margin * 2f);
            if (bc.size != nv) {
                bc.size = nv;
                // **R414-4**：这里原来还有 `bc.enabled = false; bc.enabled = true;`（想强制刷新碰撞）。
                // 但 Level.CursorBounds 与 CameraBounds 是**同一个对象**（131390 `CursorBounds = CameraBounds;`），
                // 而 Level.EnablePlacementBounds(enable) @131548 也在管 `CursorBounds.enabled`
                // （ToPlaceMode 置 true / ToPlayMode 置 false）—— 模组抢同一个 enabled 会把相位刚设好的值翻回去。
                // **enabled 交给 EnablePlacementBounds 独家管，这里只改 size。**
                // 碰撞宽相的同步靠下面的 SyncPhysics（改 size 不会自动重建 broadphase）。
                SyncPhysics();
            }
        }

        // **R414-4**：改了 Collider2D 的几何（size）之后同步一次物理宽相。
        // 不改 enabled —— 那是 Level.EnablePlacementBounds 的职责（见 GrowCollider 处说明）。
        // 游戏自己从不调 SyncTransforms（反编译里 0 命中），但它改的是**序列化的编辑器数据**，
        // 我们改的是**运行中的 collider**，必须自己同步。
        private static void SyncPhysics() {
            try { Physics2D.SyncTransforms(); }
            catch (Exception __ex) { SR.Guard.Log("同步物理宽相", __ex); }
        }

        // 硬解除。**R413-10**：这两个常量是滑块范围的**唯一真源**（Bind / 滑块回调 / 兜底都引它们）。
        //  100000 是游戏原本的量级 —— UnityPy 实测原版关卡 CameraBounds 约 105x89、自定义关卡 130x90，
        //  10 万已经是它的一千倍，够用了；上限给到 100 万（10 倍）留给铺得特别远的自制关卡。
        private const int DefaultHardRadiusInt = 100000;
        private const float DefaultHardRadius = 100000f;
        private static float HardRadius() {
            try {
                if (_radius != null && _radius.Value >= 1000) return Mathf.Clamp((float)_radius.Value, DefaultHardRadius, DefaultHardRadius * 10f);
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
        //ZoomCamera.frame 是 protected 字段（GetFrame 的结果 = 相机实际取景框）
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

        private static void ApplyHardBounds() {
            try {
                if (_on == null || !_on.Value) return;
                if (_hard != null && !_hard.Value) { _hardNote = "硬解除：关（走软路径）"; return; }
                float r = HardRadius();
                int n = 0;
                {
                    PiecePlacementCursor[] curs = Cursors();
                    for (int i = 0; i < curs.Length; i++) {
                        PiecePlacementCursor c = curs[i];
                        if (c == null) continue;
                        Bounds cb;
                        if (ReadCursorBounds(c, out cb) && !_curOrig.ContainsKey(c)) _curOrig[c] = cb;
                        // **R415**：只改 boundary（SetBounds(Bounds) 重载 @67407 就是只写这个字段）。
                        // 绝不能用 SetBounds(Collider2D) —— 那个重载 @67396 会改写 boundingCollider，
                        // 而它正是 CheckColliding.CheckBounds 的来源（@71763），一改错就再也放不下（详见 Restore 处）。
                        try { c.SetBounds(Huge(c.transform.position)); n++; } catch { }
                    }
                }
                string cam;
                {
                    // ================================================================
                    // **R418-1（用户报：解除边界依然无效；要求还原到"改相机之前"）**
                    //
                    // 还原依据：`_backup/src_beforeR411` 里的原始写法 —— 直接给
                    // `ZoomCamera` 设半径 `r` 的边界，没有 InMatch 门控。
                    // R411 之后我做过三次改动，每版都被实机否掉：
                    //   R411 加门控 -> R413 放宽到 InMatch -> R416 干脆不碰 boundary
                    //                -> R417 开回 zc.enabled，三次都不生效。
                    // 用户明确要求"还原到改相机之前"，即回到这版。
                    //
                    // 为什么这一版能生效（与 R416 拉爆的结论并不矛盾）：
                    //   · `ZoomCamera.SetBounds` @149782 改完**立刻** `frame = GetFrame()`；
                    //   · `GetFrame` @150098 对 boundary 只做单向收窄、无上限，
                    //     @150159 `if (num10 > boundary.extents.x * 2f)` 在大边界下命中 -> frame 跟着边界；
                    //   · 关键：**只要 `targets`/`boxes` 非空**，取景框由它们决定，boundary 只是收窄钳位。
                    //     R416 报"开派对盒被拉爆"是 `targets+boxes == 0` 时命中 @149953
                    //     "取景框 = boundary" 那一档；那一档只在派对盒阶段成立。
                    //   · 硬解除期间 `targets` 里有角色/光标（`AddTarget` @149810），
                    //     所以正常对局中取景由角色决定、只是被边界放宽 -> **边界真的解开了**。
                    // 也就是：这一版在"对局中"是对的，代价是"派对盒阶段"会被拉爆；
                    // 用户此刻要的是先把边界解开，故按其要求还原。
                    // ================================================================
                    ZoomCamera zc = Zc();
                    if (zc != null) {
                        if (!_zcHas) { _zcOrig = zc.GetBounds(); _zcHas = true; }
                        zc.SetBounds(Huge(LevelCenter()));   // Huge 内部已按 HardRadius() 取半径
                        cam = "相机✓";
                    } else cam = "无相机";
                }
                // R396 N1：Tick 里 ApplyHardBounds 是**每帧**调用的，这里原来每帧做 4 次连接 +
            //  2 次 int->string + 一次三元产生临时串，而 _hardNote 只被 DrawStatus 读 1 次
            //  （60 帧用 1 次）-> 加脏标记，只在值真的变了才重建（约 8.6 MB/小时的无用字符串）。
            string hardKey = ((int)r) + "|" + n + "|" + cam;
            if (hardKey != _hardNoteKey) {
                _hardNoteKey = hardKey;
                _hardNote = "硬解除 r=" + (int)r + " 光标×" + n + " " + cam;
            }
            } catch (Exception __ex) { _hardNote = "硬解除失败:" + __ex.Message; SR.Guard.Log("关卡边界-硬解除", __ex); }
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
        private static void MoveEdge(Transform t, Vector3 delta) {
            Vector3 orig;
            if (!_edgeOrig.TryGetValue(t, out orig)) { orig = t.position; _edgeOrig[t] = orig; }
            t.position = orig + delta;
        }

        // **状态行**：逐条列出真正生效的项 + 相机取景诊断
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
                Status = (_hardNote != null ? _hardNote + " | " : "") + (_softNote != null ? (_softNote + " | ") : "")
                       + string.Join("  ", bits.ToArray());
            } catch (Exception __ex) { Status = "状态生成失败：" + __ex.Message; SR.Guard.Log("关卡边界 状态", __ex); }
        }

        // **相机取景诊断**：把取景框多大 / 相机在哪 / 光标屏幕坐标 / 边界快照多大直接打出来。
        //  这是判断"界外看不见"到底是**取景框被撑爆**还是**相机没跟上**的决定性数据。
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
                    SR.LogWarn("[关卡边界] 取景框被拉得很大（" + ((int)fr.size.x) + "x" + ((int)fr.size.y)
                        + "） -  取景框被撑到这么大时，画面里所有东西都会小到看不见。");
                }
                return s;
            } catch (Exception __ex) { SR.Guard.Log("相机诊断", __ex); return "相机诊断失败"; }
        }

        // 还原：把改过的**每一项**按原值写回。
        internal static void Restore() {
            List<string> rb = new List<string>();
            try {
                int nc = 0;
                foreach (KeyValuePair<Collider2D, Vector2> kv in _colOrig) {
                    if (kv.Key == null) continue;
                    BoxCollider2D bc = kv.Key as BoxCollider2D;
                    if (bc == null) continue;
                    bc.size = kv.Value;
                    nc++;   // **R414-4**：只写 size，不碰 enabled（见 GrowCollider 处的说明）
                }
                // **R414-4（用户报：关掉解除边界后无法在正常可放置区域放置道具）**：
                // CheckColliding.FixedUpdate @18391 每物理帧执行 `InBounds = CheckBounds == null;`
                // —— 被拾取过的方块 CheckBounds 非 null，于是 InBounds 每帧被打回 false，
                // 必须靠 OnTriggerStay2D @18428 命中 CheckBounds（= CursorBounds 那个 collider）
                // 才能置回 true。改了 collider 的 size 之后 Unity 的 broadphase 不会立刻重建，
                // 碰撞对可能长时间不命中 -> CanPlaceCheck @12966 判 !flag -> 放不下去。
                // 显式同步一次物理宽相，让 trigger 关系立刻重建。
                SyncPhysics();
                _colOrig.Clear();
                rb.Add("碰撞体×" + nc);
                int ne = 0;
                foreach (KeyValuePair<global::Level, float> kv in _extraOrig) { if (kv.Key == null) continue; kv.Key.ExtraCursorBounds = kv.Value; ne++; }
                _extraOrig.Clear();
                rb.Add("Extra×" + ne);
                int ng = 0;
                foreach (KeyValuePair<Transform, Vector3> kv in _edgeOrig) { if (kv.Key == null) continue; kv.Key.position = kv.Value; ng++; }
                _edgeOrig.Clear();
                rb.Add("四边×" + ng);
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
                        // **R415**：一律 SetBounds(Bounds) —— 只还原 boundary。
                        // **不要**用 SetBounds(Collider2D) 重载：它会改写 boundingCollider，而
                        // CheckColliding.CheckBounds 绑的就是它（@71763），且**全库没有复位代码**。
                        // R413 那次误用把 boundingCollider 指到了光标自己的碰撞体上，导致
                        // 方块永远碰不到"边界" -> InBounds 恒 false -> 无法放置，且**只有切场景才能恢复**。
                        kv.Key.SetBounds(kv.Value);
                        ncur++;
                    } catch { }
                }
                _curOrig.Clear();
                rb.Add("光标原值×" + ncur);
                ZoomCamera zc = Zc();
                if (zc != null && _zcHas) { try { zc.SetBounds(_zcOrig); rb.Add("相机=原快照"); } catch { } }
                _zcHas = false;
                //**R417**：还原时把 ZoomCamera 的开关也恢复原状
                // （关边界后 FixCameraToBoundaries 会重新接管并自己把 ZoomCamera 关掉；
                //  这里只在"原值本来是关的（是我们打开的）"时才关回去）。
                try { ZoomCamera zr = Zc(); if (zr != null && !_zcWasEnabled && zr.enabled) zr.enabled = false; } catch { }
                Status = "（已还原为游戏原值）" + string.Join(" ", rb.ToArray());
            } catch (Exception __ex) { Status = "（还原异常：" + __ex.Message + "）已还原：" + string.Join(" ", rb.ToArray()); SR.Guard.Log("关卡边界 还原", __ex); }
            _hardNote = null;
            _softNote = null;
        }

        //换场景：Level / 相机 / 光标 / 方块全是新对象，旧原值字典必须整体作废
        private static void Reset() {
            _colOrig.Clear();
            _extraOrig.Clear();
            _edgeOrig.Clear();
            _fixWas.Clear();
            _curOrig.Clear();
            _igWas.Clear();
            for (int i = 0; i < 3; i++) { _wallCol[i] = null; }
            _zc = null; _zcAt = -9999;
            _zcHas = false;
            _cursors = _noCursors; _cursorsAt = -9999;
            _lvCache = null; _lvCacheAt = -9999;
            _wallScanAt = -9999;   // **R396 S1**：两个时钟都要归零
            _wallScanTries = 0; _wallScanScene = -1;   // **P1-07**：换场景也把"扫描次数上限"清零，重新给机会
            _fixers = new FixCameraToBoundaries[0]; _fixersAt = -9999;
            _hardNote = null;
            _softNote = null;
            _camDiagDone = false;
            _applied = false;
            Status = "（换场景：已重置，等待下次应用）";
        }

        // **R413-8/10**：边界两项的滑块范围。
        //  · 半径范围改成"原值 ~ 原值x10"（100000 ~ 1000000），默认就是原值。
        //  · 边界外扩沿用 200-1000（那是"每边多扩多少"的世界单位，与半径不同量级，不该一起放大）。
        //  · fmt 用千分位友好格式：半径跨度大，"0" 会显示成 1000000 那种长串，宽度按实测给。
        private static void Dismiss() {
            try { if (_applied) { Restore(); _applied = false; } }
            catch (Exception __ex) { SR.Guard.Log("关卡边界 收起", __ex); }
        }
    }
    // 解除 UI 3 覆盖（黑域的真凶，R387 用户实测确认）
    // **黑域根因**：BlankLevel 场景里有 4 个纯黑 whiteSquare（color 0,0,0,1），
    //   sortingLayerID = 519308545 = UI 3（uniqueID 最大 = 排最顶）-> 盖住界外一切。
    //   本功能把它们（以及 UI3 上任何 Renderer）停画。
    public class RemoveUI3 : ITweak {
        private static ConfigEntry<bool> _on;
        private static readonly Dictionary<Renderer, bool> _wasOn = new Dictionary<Renderer, bool>();
        internal static string Note = "";
        private static int _frame = -9999;
        private static int _hitCount = -1;
        private static float _nextScanAt;    //下次全扫时间（V6：不再按帧号取模）
        private static string _scanScene;    //上次扫描时的场景名（换场景立即重扫）

        private static void SelfReg() {
            SR.LocKey("Experiments", "Remove UI3", "解除 UI3 覆盖", "Remove UI3 overlay");
            SR.LocDesc("Experiments", "Remove UI3",
                "界外那片黑的真凶就是 UI3 层. 那一层排在所有层最上面, 上面有几个纯黑方块把界外全遮住了. 打开就不画它们, 关掉恢复. 只想确认现象的话单开这一项试试.",
                "Stops drawing everything on the UI 3 sorting layer. The black squares there are what makes"
              + " the area outside the level look black.");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _on = plugin.Config.Bind("Experiments", "Remove UI3", false,
                    "解除 UI 3 覆盖：停画 UI3 层所有 Renderer（黑域的真正元凶）。");
                SR.RegisterTick("解除UI3", Tick);
                SR.RegisterSceneHook(Reset);
                SR.RegisterDismissHook(Restore);
            } catch (Exception __ex) { SR.Guard.Log("解除UI3 初始化", __ex); }
        }

        // **R396 S3**：换场景时必须清 _wasOn。
        //  它是 Dictionary<Renderer, bool>（强引用），而 Restore() 只在"功能关闭/总开关关闭"时才跑。
        //  功能常开时每换一个场景就把新场景全部 Renderer 灌进去，旧的（已随场景销毁）一个都不清
        //  -> 逐场累积一批已销毁 Renderer 的强引用，关闭该功能时一次明显卡顿。
        //  换场景后旧 Renderer 全部已销毁，"原值"已无意义，直接清空。
        private static void Reset() { _hitCount = -1; _scanScene = null; _nextScanAt = 0f; Note = ""; _wasOn.Clear(); }

        private static void Tick() {
            try {
                if (_on == null) return;
                if (_frame == Time.frameCount) return;
                _frame = Time.frameCount;
                if (!_on.Value) { Restore(); return; }
                if (!SR.GateMaster) { Restore(); return; }
                Apply();
            } catch (Exception __ex) { SR.Guard.Log("解除UI3 Tick", __ex); }
        }

        private static void Apply() {
            try {
                // 扫描时机：**首次 / 换场景立即全扫**，之后每 2.5 秒复查一次。
                // **R396 N6**：原来无脑每 30 帧一次 FindObjectsOfType<Renderer>()（关卡里数百到数千个）
                //  + 同等规模的数组分配。
                // **V6**：上一版注释写"只在换场景重建一次"，代码却是 `frameCount % 120` —— 按帧号取模
                //  在 60fps 是 2 秒、144fps 只有 0.8 秒，间隔随帧率漂。现在改成按 unscaled 时间，
                //  并且换场景必定立即重扫；复查只为覆盖"加载完成后才生成"的对象（保留这层兜底）。
                string sc = "";
                try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                float now = Time.unscaledTime;
                if (_hitCount >= 0 && sc == _scanScene && now < _nextScanAt) return;
                _scanScene = sc;
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
            } catch (Exception __ex) { SR.Guard.Log("应用UI3解除", __ex); }
        }

        // UI 3 的显示名可能带空格；Unity 的 sortingLayerName 就是配置里的原样字符串
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
