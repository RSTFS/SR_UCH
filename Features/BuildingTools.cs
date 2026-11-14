// 建造增强：自由放置（0.01 网格吸附）+ 无视碰撞放置 + 建造上限放宽（BuildUnlimiter）。
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
        private static IFeatureHost _mp;   // 只用来读 Config
        private static bool _collisionOverride;
        private static ConfigEntry<bool> _collisionEntry;
        private static ConfigEntry<KeyCode> _toggleCollisionKey;
        // 自由放置：方块吸附 0.01 单位而非 1 单位网格。
        private static bool _gridOverride;
        private static ConfigEntry<bool> _gridEntry;
        private static ConfigEntry<KeyCode> _toggleGridKey;
        // 运行期开关：游戏内管理器也可改。
        public static bool Enabled = true;
        public static bool GridOverride { get { return _gridOverride; } set { _gridOverride = value; } }

        private static void SelfReg() {
            // 本文件反射的游戏私有成员：改名时启动自检会告警。
            SRApi.Ref.Override("PiecePlacementCursor.SetPiece", "自由放置（改写放置结果）");
            SRApi.Ref.Override("PiecePlacementCursor.FixedUpdate", "自由放置（补丁目标）");
            SRApi.Ref.Override("PiecePlacementCursor.heldPositionOffset", "自由放置（关闭网格吸附）");
            SRApi.RowCompanions.Add(BuilderCompanion); // 覆盖项与快捷键同行显示
            SRApi.RowFilters.Add(BuilderRowVisible);   // 开关键位不单独成行
            // 建造页控件紧贴标签（标签长度差很大，需按实测宽度排版）。
            SRApi.RowCompact.Add(delegate(ConfigEntryBase e) {
                return e != null && e.Definition.Section == "build";
            });
            SRApi.RegisterPage("build", RenderPage); // 自绘页
            SRApi.LocSec("build", "建造", "Build");
            SRApi.Nav("build", 60); // 侧栏顺序
            SRApi.LocKey("build", "Collision Override", "无视碰撞", null);
            SRApi.LocDesc("build", "Collision Override", "无视碰撞放置\n方块可与任何东西重叠、悬空、穿插\n只影响放置判定，不改物理与玩法", "Ignore-collision placement\npieces may overlap, float or intersect anything\nplacement rules only; physics unchanged");
            SRApi.LocKey("build", "Collision Toggle Key", "无视碰撞开关", null);
            SRApi.LocDesc("build", "Collision Toggle Key", "无视碰撞按键\n对局内临时开关无视碰撞放置\n可改绑，支持组合键", "Ignore-collision key\ntoggles ignore-collision placement in-game\nrebindable, combos supported");
            SRApi.LocKey("build", "Grid Override", "自由放置", null);
            SRApi.LocDesc("build", "Grid Override", "自由放置\n吸附从 1 格改为 0.01 格，可微调位置\n只在建造阶段生效，关闭立即恢复整格", "Free placement\nsnapping goes from 1 unit to 0.01 for fine-tuning\nbuild phase only; off restores 1-unit snapping");
            SRApi.LocKey("build", "Grid Toggle Key", "自由放置开关", null);
            SRApi.LocDesc("build", "Grid Toggle Key", "自由放置按键\n对局内临时开关自由放置\n可改绑，支持组合键", "Free placement key\ntoggles free placement in-game\nrebindable, combos supported");
        }

        // 当前满度上限（只读框用）：直接读游戏字段，不借其它功能接口。
        private static int CurrentCapShown() {
            try { return GameSettings.GetInstance().LevelFullnessScoreLimit; } catch { return 500; }
        }

        // 覆盖条目的同行同伴：该覆盖项的行内再渲染一个开关键位控件。
        private static string BuilderCompanion(ConfigEntryBase e) {
            if (e.Definition.Section != "build") return null;
            if (e.Definition.Key == "Collision Override") return "Collision Toggle Key";
            if (e.Definition.Key == "Grid Override") return "Grid Toggle Key";
            return null;
        }

        private static bool BuilderRowVisible(ConfigEntryBase e) {
            if (e.Definition.Section != "build") return true;
            return e.Definition.Key != "Collision Toggle Key" && e.Definition.Key != "Grid Toggle Key";
        }

        // 自绘页：按分区标题 + 条目逐行渲染（分区判据取自 Bind 的键名）。
        private static bool IsEnhanceKey(string k) { return k == "Collision Override" || k == "Grid Override"; }
        private static bool IsLimitKey(string k) { return k == "Lift Build Cap" || k == "Build Cap Value"; }
        private static void RenderPage() {
            float col = Mathf.Max(SRApi.Ctl.Sc(180), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SRApi.Ctl.SectionEntries("build");
            bool any = false;


            // 分区一：建造（无视碰撞 / 自由放置）
            SRApi.Ctl.SectionTitle("- " + SRApi.T("建造", "Build") + " -",
                SRApi.T("建造增强：让方块无视放置规则、按更细的网格自由微调摆放。",
                        "Build: place pieces ignoring the placement rules and snap on a finer grid."));
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (!IsEnhanceKey(e.Definition.Key)) continue;
                any = true;
                SRApi.Ctl.DrawEntryRow(e, col);
            }

            GUILayout.Space(SRApi.Ctl.Sc(6));
            SRApi.Ctl.SectionTitle("- " + SRApi.T("建造上限", "Build Limit") + " -", SRApi.T("建造上限（BuildUnlimiter）：解除树屋保存/发布界面的关卡满度限制，超满的关卡也能正常发布/上传。",
                     "Build Limit (BuildUnlimiter): lifts the treehouse save/publish fullness cap so over-full levels can be published."));
            GUILayout.BeginHorizontal();
            // GUILayout.Label 不会收缩到文字宽度，宽度需按实测文字尺寸。
            float lw = SRApi.Ctl.Label.CalcSize(SRApi.Ctl.Content(SRApi.T("当前上限", "Current limit"))).x + SRApi.Ctl.Sc(12);
            GUILayout.Label(SRApi.Ctl.Content(SRApi.T("当前上限", "Current limit"), SRApi.T("当前生效的满度上限；游戏原版为 500", "The current effective fullness cap; vanilla is 500")),
                SRApi.Ctl.Label, GUILayout.Width(Mathf.Max(SRApi.Ctl.Sc(60), lw)), GUILayout.Height(SRApi.Ctl.Sc(26)));
            GUILayout.Space(SRApi.Ctl.Sc(6));
            bool oldEn = GUI.enabled;
            try {
            GUI.enabled = false;
            // SRApi.T 两个参数都会被求值；前缀不变，故只拼一次。
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

            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (IsLimitKey(e.Definition.Key)) continue;
                if (IsEnhanceKey(e.Definition.Key)) continue;   // 分区一已渲染
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
            SRApi.RegisterKey("建造增强-碰撞开关", _toggleCollisionKey, "press", delegate {
                if (!SRApi.GateMaster || !Enabled) return;
                _collisionEntry.Value = !_collisionEntry.Value; Notify(SRApi.T("无视碰撞", "Collision Override"), _collisionEntry.Value);
            });
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
            SRApi.RegisterKey("建造增强-自由放置开关", _toggleGridKey, "press", delegate {
                if (!SRApi.GateMaster || !Enabled) return;
                _gridEntry.Value = !_gridEntry.Value; Notify(SRApi.T("自由放置", "Free Placement"), _gridEntry.Value);
            });

            // 补丁分两次独立注册：FreePlacementPatch 是顶层类不会被连带注册，且能独立失败互不连累。
            try { Harmony.CreateAndPatchAll(typeof(BuildingTools)); }
            catch (Exception e) { SRApi.LogWarn("BuildingTools 补丁失败: " + e.Message); }
            try { Harmony.CreateAndPatchAll(typeof(FreePlacementPatch)); }
            catch (Exception __fp) { SRApi.LogError("自由放置 补丁注册失败: " + __fp.Message); }
        }

        private static void Notify(string name, bool state) {
            SRApi.Notify(name + (state ? SRApi.T("：开", ": ON") : SRApi.T("：关", ": OFF")));   // 走核心通道
        }

        [HarmonyPatch(typeof(PiecePlacementCursor), "ReceiveEvent")]
        [HarmonyPostfix]
        static void OnPieceInput(PiecePlacementCursor __instance) {
            // Harmony 会吞掉补丁异常，故统一兜住并经 Guard.Log（按调用点去重）留痕。
            try {
                        if (!SRApi.GateMaster) return;
                        if (!Enabled) return;
                        if (SRApi.Typing) return; 
                        if (__instance.Piece == null) return;
                        __instance.Piece.IgnorePlacementRules = _collisionOverride;
            } catch (Exception __ex) { SRApi.Guard.Log("自由放置：忽略放置规则", __ex); }
        }

    }

    // 自由放置：Postfix 把方块位置改为未取整的光标位置。
    [HarmonyPatch]
    internal static class FreePlacementPatch {
            private static FieldInfo _heldOffsetField;
            static IEnumerable<MethodBase> TargetMethods() {
                // 逐个判空后再 yield：Harmony 遍历到 null 会抛异常，导致自由放置整体失效。
                MethodInfo m = SRApi.Ref.Method(typeof(PiecePlacementCursor), "SetPiece", "自由放置（改写放置结果）");
                if (m != null) yield return m;
                m = SRApi.Ref.Method(typeof(PiecePlacementCursor), "FixedUpdate", "自由放置（补丁目标）");
                if (m != null) yield return m;
                // 子类覆盖 FixedUpdate 且不调 base 时会漏掉，故把派生光标也列为目标（取不到就跳过）。
                m = SRApi.Ref.Method(typeof(PartyCursor), "FixedUpdate", "自由放置（派生光标）");
                if (m != null) yield return m;
            }
            static void Postfix(PiecePlacementCursor __instance) {
                try {
                    if (!SRApi.GateMaster) return; // 总开关关闭时失效
                    if (!BuildingTools.GridOverride) return;
                    if (__instance == null || __instance.Piece == null) return;
                    // 复刻游戏 FixedUpdate 的守卫：这些状态下游戏刻意不动位置，照写会拽到非法位置。
                    if (SafeBool(__instance, "disabled")) return;
                    if (__instance.WaitingForPlaceMessageResponse) return;
                    if (SafeBool(__instance, "waitingForFixedUpdate")) return;
                    if (SafeBool(__instance, "tryingToCancel")) return;
                    if (SafeBool(__instance, "placementPhysicsLock")) return;
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
                    if (__instance.Piece.ConstrainX) freePos = new Vector2(cur.x, freePos.y);
                    else if (__instance.Piece.ConstrainY) freePos = new Vector2(freePos.x, cur.y);
                    __instance.Piece.transform.position = new Vector3(freePos.x, freePos.y, cur.z);
                } catch (Exception __ex) { SRApi.Guard.Log("BuildingTools.Vector3", __ex); }
            }

            // 读 private/protected bool 字段，取不到按 false（字段改名只是少一条守卫）。
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

// 分区：建造上限（BuildUnlimiter）
namespace SR_UCH.Tweaks {
    // 放宽 GameSettings.LevelFullnessScoreLimit。
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
            SRApi.LocDesc("build", "Lift Build Cap", "满度上限解除\n把树屋保存与发布的满度上限提到设定值\n关闭后恢复原版 500", "Unlock build cap\nraises the treehouse save/publish fullness cap\nwhen off, the vanilla 500 applies");
            SRApi.LocKey("build", "Build Cap Value", "上限数值", "Limit Value");
            SRApi.LocDesc("build", "Build Cap Value", "满度上限数值\n解除后使用的上限值\n范围 500-10000000，改完立即生效", "Build cap value\ncap used while the unlock is on\n500-10000000; applies immediately");
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
            Apply(); // 启动归位
            // 启动时 GameSettings 可能未加载，故挂场景切换重试。
            SceneManager.activeSceneChanged += (a, b) => Apply();
            // Apply() 依赖 GateMaster：DismissHook 管关总开关，SceneHook 管早期未就绪重试。
            SRApi.RegisterDismissHook(delegate { if (!SRApi.GateMaster) Apply(); });
            SRApi.RegisterSceneHook(delegate { if (!SRApi.GateMaster) Apply(); });
        }

        private static void Apply() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return; // 场景未加载完，等下次场景切换重试
                gs.LevelFullnessScoreLimit = (SRApi.GateMaster && Enabled) ? LimitValue : VanillaLimit;
                // 只写游戏字段，配置落盘由 SRApi.FlushConfig 节流。
            } catch (Exception __ex) {
                SRApi.Guard.Log("应用建造上限", __ex);
            }
        }
    }
}
