// 建造增强：自由放置（网格覆盖，吸附到 0.01 单位）+ 建造上限放宽（BuildUnlimiter）。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace SR_UCH.Tweaks {
    [SrFeature("sr.buildingtools", "BuildingTools", "1.0")]
    public class BuildingTools : ITweak {
        private static IFeatureHost _mp;   // 只用来读 Config（契约类型）
        private static bool _collisionOverride;
        private static ConfigEntry<bool> _collisionEntry;
        private static ConfigEntry<KeyCode> _toggleCollisionKey;
        // 自由放置（网格覆盖）：方块吸附 0.01 单位而非 1 单位网格。
        private static bool _gridOverride;
        private static ConfigEntry<bool> _gridEntry;
        private static ConfigEntry<KeyCode> _toggleGridKey;
        // 运行期开关：游戏内管理器同时也可改。
        public static bool Enabled = true;
        public static bool GridOverride { get { return _gridOverride; } set { _gridOverride = value; } }

        private static void SelfReg() {
            // 本文件反射的游戏私有成员，改名时启动自检会在日志里告警。
            SRApi.Ref.Override("PiecePlacementCursor.SetPiece", "自由放置（改写放置结果）");
            SRApi.Ref.Override("PiecePlacementCursor.FixedUpdate", "自由放置（补丁目标）");
            SRApi.Ref.Override("PiecePlacementCursor.heldPositionOffset", "自由放置（关闭网格吸附）");
            SRApi.RowCompanions.Add(BuilderCompanion); // 通用条目行扩展点：覆盖项与快捷键同行显示
            SRApi.RowFilters.Add(BuilderRowVisible);   // 两个开关键位不单独成行
            SRApi.RegisterPage("build", RenderPage); // 本功能页自绘（含建造上限分区）
            SRApi.LocSec("build", "建造", "Build");
            SRApi.Nav("build", 50); // 侧栏栏目顺序 50
            SRApi.LocKey("build", "Collision Override", "无视碰撞", null);
            SRApi.LocDesc("build", "Collision Override", "无视碰撞放置开关\n放开放置规则，方块可重叠、悬空或相互穿插\n仅影响放置判定，不改变物理与玩法\n关", "ignore-collision placement switch\npieces may overlap, float or intersect anything\nplacement rules only; physics and gameplay are unchanged\nOFF");
            SRApi.LocKey("build", "Collision Toggle Key", "无视碰撞开关", null);
            SRApi.LocDesc("build", "Collision Toggle Key", "无视碰撞快捷键\n游戏中临时开关无视碰撞放置\n可改绑，支持组合键\n默认：F1", "ignore-collision key\ntemporarily toggles ignore-collision placement in-game\nrebindable, combos supported\nDefault: F1");
            SRApi.LocKey("build", "Grid Override", "自由放置", null);
            SRApi.LocDesc("build", "Grid Override", "自由放置开关\n吸附精度由 1 格改为 0.01 格，可微调位置\n仅影响放置取整，只在建造阶段生效；关闭立即回到整格吸附\n关", "free placement switch\nchanges grid snapping from 1 unit to 0.01 units for fine-tuning\nplacement rounding only, build phase only; turning it off restores 1-unit snapping immediately\nOFF");
            SRApi.LocKey("build", "Grid Toggle Key", "自由放置开关", null);
            SRApi.LocDesc("build", "Grid Toggle Key", "自由放置快捷键\n游戏中临时开关自由放置\n可改绑，支持组合键\n默认：F2", "free placement key\ntemporarily toggles free placement in-game\nrebindable, combos supported\nDefault: F2");
        }

        // 当前生效的满度上限（只读框显示用）：直接读游戏字段，不借其它功能的接口以避免横向依赖。
        // 读不到时回退游戏原版上限 500。
        private static int CurrentCapShown() {
            try { return GameSettings.GetInstance().LevelFullnessScoreLimit; } catch { return 500; }
        }

        // 覆盖条目的同行同伴：其开关键位（通用条目行按注册的 key 再渲染一个控件）。
        private static string BuilderCompanion(ConfigEntryBase e) {
            if (e.Definition.Section != "build") return null;
            if (e.Definition.Key == "Collision Override") return "Collision Toggle Key";
            if (e.Definition.Key == "Grid Override") return "Grid Toggle Key";
            return null;
        }

        // 两个开关键位并进覆盖行显示，不单独成行。
        private static bool BuilderRowVisible(ConfigEntryBase e) {
            if (e.Definition.Section != "build") return true;
            return e.Definition.Key != "Collision Toggle Key" && e.Definition.Key != "Grid Toggle Key";
        }

        // 自绘页：按分区标题 + 条目逐行渲染（分区判据取自 Initialize 里 Bind 的键名）。
        private static bool IsEnhanceKey(string k) { return k == "Collision Override" || k == "Grid Override"; }
        private static bool IsLimitKey(string k) { return k == "Lift Build Cap" || k == "Build Cap Value"; }
        private static void RenderPage() {
            float col = Mathf.Max(SRApi.Ctl.Sc(180), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SRApi.Ctl.SectionEntries("build");
            bool any = false;

            // 分区按条目键显式划分，不依赖遍历顺序（SectionEntries 按键名排序，顺序与分区无关）。

            // 分区一：建造增强
            SRApi.Ctl.SectionTitle("- " + SRApi.T("建造", "Build") + " -", SRApi.T("建造增强：让方块无视放置规则、按更细的网格自由微调摆放。",
                     "Build: place pieces ignoring the placement rules and snap on a finer grid."));
            GUILayout.Space(SRApi.Ctl.Sc(2));
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (!IsEnhanceKey(e.Definition.Key)) continue;
                any = true;
                SRApi.Ctl.DrawEntryRow(e, col);
            }

            // 分区二：建造上限
            GUILayout.Space(SRApi.Ctl.Sc(6));
            SRApi.Ctl.SectionTitle("- " + SRApi.T("建造上限", "Build Limit") + " -", SRApi.T("建造上限（BuildUnlimiter）：解除树屋保存/发布界面的关卡满度限制，超满的关卡也能正常发布/上传。",
                     "Build Limit (BuildUnlimiter): lifts the treehouse save/publish fullness cap so over-full levels can be published."));
            GUILayout.Space(SRApi.Ctl.Sc(2));
            // 该行为手工排版：标签占名称列宽度 + 间隔 6 + 主控件列，与下方两行的编辑框左边缘对齐。
            GUILayout.BeginHorizontal();
            // GUILayout.Label 不会像 RestoreLabel 那样收缩到文字宽度，宽度需按文字实测。
            float lw = SRApi.Ctl.Label.CalcSize(new GUIContent(SRApi.T("当前上限", "Current limit"))).x + SRApi.Ctl.Sc(12);
            GUILayout.Label(new GUIContent(SRApi.T("当前上限", "Current limit"),
                SRApi.T("当前生效的满度上限；游戏原版为 500", "The current effective fullness cap; vanilla is 500")),
                SRApi.Ctl.Label, GUILayout.Width(Mathf.Max(SRApi.Ctl.Sc(60), lw)), GUILayout.Height(SRApi.Ctl.Sc(26)));
            GUILayout.Space(SRApi.Ctl.Sc(6));
            bool oldEn = GUI.enabled;
            try {
            GUI.enabled = false;
            // SRApi.T(zh, en) 的两个参数都会被求值：前缀为常量、只有末尾数字在变，故字符串只拼一次。
            int curLimit = CurrentCapShown();
            GUILayout.TextField(SRApi.T("当前上限：", "Current: ") + curLimit,
                SRApi.Ctl.SearchBox, GUILayout.Width(SRApi.Ctl.Sc(170)), GUILayout.Height(SRApi.Ctl.Sc(26)));
            } finally { GUI.enabled = oldEn; }
            GUILayout.EndHorizontal();
            GUILayout.Space(SRApi.Ctl.Sc(2));
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (!IsLimitKey(e.Definition.Key)) continue;
                any = true;
                SRApi.Ctl.DrawEntryRow(e, col);
            }

            // 其余条目：避免出现未渲染的条目
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (IsEnhanceKey(e.Definition.Key) || IsLimitKey(e.Definition.Key)) continue;
                any = true;
                SRApi.Ctl.DrawEntryRow(e, col);
            }
            if (!any) GUILayout.Label(SRApi.T("（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label);
        }

        public void Initialize(IFeatureHost plugin) {
            SelfReg();
            _mp = plugin;
            _collisionEntry = _mp.Config.Bind(
                "build",
                "Collision Override",
                false,
                "Whether pieces ignore placement rules (can be placed anywhere) at startup");
            _collisionOverride = _collisionEntry.Value;
            _collisionEntry.SettingChanged += (s, e) => _collisionOverride = _collisionEntry.Value;
            _toggleCollisionKey = _mp.Config.Bind(
                "build",
                "Collision Toggle Key",
                KeyCode.F1,
                "Key to toggle the collision override in-game");
            SRApi.RegisterKey("建造增强-碰撞开关", _toggleCollisionKey, "toggle");
            _gridEntry = _mp.Config.Bind(
                "build",
                "Grid Override",
                false,
                "Whether pieces snap to a fine 0.01 grid instead of the 1-unit build grid (free placement)");
            _gridOverride = _gridEntry.Value;
            _gridEntry.SettingChanged += (s, e) => _gridOverride = _gridEntry.Value;
            _toggleGridKey = _mp.Config.Bind(
                "build",
                "Grid Toggle Key",
                KeyCode.F2,
                "Key to toggle free placement (grid override) in-game");
            SRApi.RegisterKey("建造增强-自由放置开关", _toggleGridKey, "toggle");

            // 嵌套类的 CreateAndPatchAll 不递归，需显式注册；单个失败不影响其他。
            try { Harmony.CreateAndPatchAll(typeof(BuildingTools)); } catch (Exception e) { SRApi.LogWarn("BuildingTools 补丁失败: " + e.Message); }
            try { Harmony.CreateAndPatchAll(typeof(FreePlacementPatch)); } catch (Exception e) { SRApi.LogError("自由放置补丁注册失败: " + e.Message); }
        }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void ToggleKeys() {
            if (!SRApi.GateMaster) return;
            if (!Enabled) return;
            if (SRApi.UiOpen && SRApi.BlockInput) return; 
            if (SRApi.ComboKeyDown(_toggleCollisionKey)) { _collisionEntry.Value = !_collisionEntry.Value; Notify(SRApi.T("无视碰撞", "Collision Override"), _collisionEntry.Value); }
            if (SRApi.ComboKeyDown(_toggleGridKey)) { _gridEntry.Value = !_gridEntry.Value; Notify(SRApi.T("自由放置", "Free Placement"), _gridEntry.Value); }
        }

        private static void Notify(string name, bool state) {
            SRApi.Notify(name + (state ? SRApi.T("：开", ": ON") : SRApi.T("：关", ": OFF")));   // 走核心通道，便于日志追溯
        }

        [HarmonyPatch(typeof(PiecePlacementCursor), "ReceiveEvent")]
        [HarmonyPostfix]
        static void OnPieceInput(PiecePlacementCursor __instance) {
            // Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 SRApi.Guard.Log（按调用点去重）留痕。
            try {
                        if (!SRApi.GateMaster) return;
                        if (!Enabled) return;
                        if (SRApi.UiOpen && SRApi.BlockInput) return; 
                        if (__instance.Piece == null) return;
                        __instance.Piece.IgnorePlacementRules = _collisionOverride;
            } catch (Exception __ex) { SRApi.Guard.Log("自由放置：忽略放置规则", __ex); }
        }

        // 自由放置：SetPiece/FixedUpdate 后用 Postfix 把方块位置改为未取整的光标位置。
        [HarmonyPatch]
        static class FreePlacementPatch {
            private static FieldInfo _heldOffsetField;
            static IEnumerable<MethodBase> TargetMethods() {
                // 逐个判空后再 yield：Harmony 遍历到 null 会抛异常，导致自由放置整体失效。
                MethodInfo m = SRApi.Ref.Method(typeof(PiecePlacementCursor), "SetPiece", "自由放置（改写放置结果）");
                if (m != null) yield return m;
                m = SRApi.Ref.Method(typeof(PiecePlacementCursor), "FixedUpdate", "自由放置（补丁目标）");
                if (m != null) yield return m;
            }
            static void Postfix(PiecePlacementCursor __instance) {
                try {
                    if (!SRApi.GateMaster) return; // 总开关关闭时失效
                    if (!GridOverride) return;
                    if (__instance == null || __instance.Piece == null) return;
                    // 复刻游戏 FixedUpdate 的守卫：这些状态下游戏刻意不动位置，照写会把方块拽到非法位置。
                    // disabled 等为 protected，经 SafeBool 读取。
                    if (SafeBool(__instance, "disabled")) return;
                    if (__instance.WaitingForPlaceMessageResponse) return;
                    if (SafeBool(__instance, "waitingForFixedUpdate")) return;
                    if (SafeBool(__instance, "tryingToCancel")) return;
                    if (SafeBool(__instance, "placementPhysicsLock")) return;
                    // 自由位置 = 光标位置 + 手持偏移 + 变体偏移（不取整）
                    Vector2 offset = __instance.Piece.GetTransformedPlacementOffset();
                    Vector3 cp = __instance.transform.position;
                    if (_heldOffsetField == null)
                        _heldOffsetField = SRApi.Ref.Field(typeof(PiecePlacementCursor), "heldPositionOffset", "自由放置（关闭网格吸附）");
                    Vector3 held = Vector3.zero;
                    if (_heldOffsetField != null) {
                        try { held = (Vector3)_heldOffsetField.GetValue(__instance); } catch (Exception __ex) { SRApi.Guard.Log("BuildingTools.Field", __ex); }
                    }
                    Vector2 freePos = new Vector2(cp.x + held.x + offset.x, cp.y + held.y + offset.y);
                    Vector3 cur = __instance.Piece.transform.position;
                    // 受约束件只能沿一个轴移动，保留另一轴当前值。
                    if (__instance.Piece.ConstrainX) freePos = new Vector2(cur.x, freePos.y);
                    else if (__instance.Piece.ConstrainY) freePos = new Vector2(freePos.x, cur.y);
                    __instance.Piece.transform.position = new Vector3(freePos.x, freePos.y, cur.z);
                } catch (Exception __ex) { SRApi.Guard.Log("BuildingTools.Vector3", __ex); }
            }

            // 读取 private/protected bool 字段，取不到按 false（字段改名只是少一条守卫）。
            private static bool SafeBool(object target, string field) {
                try {
                    System.Reflection.FieldInfo fi = SRApi.Ref.Field(target.GetType(), field, "建造增强：自由放置守卫");
                    if (fi == null) return false;
                    object v = fi.GetValue(target);
                    return v is bool && (bool)v;
                } catch { return false; }
            }
        }
    }
}

// 以下由 BuildUnlimiter.cs 并入（UI 在本文件，配置与实现同归一处）。
namespace SR_UCH.Tweaks {
    // 建造上限：放宽 GameSettings.LevelFullnessScoreLimit。
    [SrFeature("sr.buildunlimiter", "BuildUnlimiter", "1.0")]
    public class BuildUnlimiter : ITweak {
        public const int VanillaLimit = 500;
        private const int DefaultLimit = 1000000;
        private const int MinLimit = 500;
        private const int MaxLimit = 10000000;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _limitEntry;

        public static bool Enabled { get { return _enabled != null && _enabled.Value; } }

        public static int LimitValue {
            get { return _limitEntry != null ? Mathf.Clamp(_limitEntry.Value, MinLimit, MaxLimit) : DefaultLimit; }
        }

        private static void SelfReg() {
            SRApi.LocKey("build", "Lift Build Cap", "解除建造上限", null);
            SRApi.LocDesc("build", "Lift Build Cap", "满度上限解除开关\n把关卡满度上限从原版 500 提升到设定值\n用于树屋保存/发布；关闭后恢复 500\n关", "build cap unlock switch\nraises the level fullness cap from the vanilla 500 to the configured value\napplies to treehouse save/publish; turning it off restores 500\nOFF");
            SRApi.LocKey("build", "Build Cap Value", "上限数值", "Limit Value");
            SRApi.LocDesc("build", "Build Cap Value", "满度上限数值\n设置解除后的满度上限\n范围 500 到 10000000，改完立即生效\n默认：1000000", "build cap value\nsets the fullness cap used when the unlock is on\nrange 500-10000000, applies immediately\nDefault: 1000000");
        }

        public void Initialize(IFeatureHost plugin) {
            SelfReg();
            _enabled = plugin.Config.Bind("build", "Lift Build Cap", false,
                "解除关卡满度限制：开启后树屋保存/发布界面的满度上限从原版 500 提高到上限数值（默认 1000000），超满的关卡也能正常发布/上传；关闭立即恢复原版。");
            _limitEntry = plugin.Config.Bind("build", "Build Cap Value", DefaultLimit, new ConfigDescription(
                "满度上限数值（500 - 10000000；游戏原版为 500）", new AcceptableValueRange<int>(MinLimit, MaxLimit)));
            if (_limitEntry.Value < MinLimit || _limitEntry.Value > MaxLimit) {
                _limitEntry.Value = DefaultLimit;
            }
            _enabled.SettingChanged += (s, e) => Apply();
            _limitEntry.SettingChanged += (s, e) => Apply();
            Apply(); // 启动时归位：关闭则保持原版 500
            // 启动时 GameSettings 可能尚未加载（GetInstance() 返回 null），故挂场景切换重试；
            // 与 DestroyBlocks / Respawn / ExModule 等其它 tweak 的做法一致。
            SceneManager.activeSceneChanged += (a, b) => Apply();
            // Apply() 的求值依赖 SRApi.GateMaster，总开关关闭时需要有人调用它。
            // DismissHook 负责关总开关，SceneHook 负责启动早期 GameSettings 未就绪时的重试。
            SRApi.RegisterDismissHook(delegate { if (!SRApi.GateMaster) Apply(); });
            SRApi.RegisterSceneHook(delegate { if (!SRApi.GateMaster) Apply(); });
        }

        private static void Apply() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return; //场景还没加载完：交给下次场景切换重试（见 Initialize 里的订阅）
                gs.LevelFullnessScoreLimit = (SRApi.GateMaster && Enabled) ? LimitValue : VanillaLimit;
                // 只写游戏字段，不调 Config.Save（配置落盘统一由 SRApi.FlushConfig 节流）。
            } catch (Exception __ex) {
                SRApi.Guard.Log("应用建造上限", __ex);
            }
        }
    }
}
