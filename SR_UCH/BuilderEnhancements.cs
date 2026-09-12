using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public class BuilderEnhancements : ITweak {
        private static MainPlugin _mp;
        private static bool _collisionOverride;
        private static ConfigEntry<bool> _collisionEntry;
        private static ConfigEntry<KeyCode> _toggleCollisionKey;
        //自由放置（网格覆盖）：方块不再吸附 1 单位网格，改为吸附 0.01 单位，可自由微调摆放
        private static bool _gridOverride;
        private static ConfigEntry<bool> _gridEntry;
        private static ConfigEntry<KeyCode> _toggleGridKey;
        //runtime toggles (also controlled by the in-game manager)
        public static bool Enabled = true;
        public static bool CollisionOverride { get { return _collisionOverride; } set { _collisionOverride = value; } }
        public static bool GridOverride { get { return _gridOverride; } set { _gridOverride = value; } }

        private static void SelfReg() {
            SR.RowCompanions.Add(BuilderCompanion); //通用条目行扩展点：「覆盖」与它的快捷键同行显示
            SR.RowFilters.Add(BuilderRowVisible);   //两个开关键位不单独成行
            SR.RegisterPage("Builder Enhancements", RenderPage); //本功能页自绘（含建造上限小分区）
            SR.LocSec("Builder Enhancements", "建造", "Builder");
            SR.Nav("Builder Enhancements", 40); //侧栏顺序 40
            SR.LocKey("Builder Enhancements", "Collision Override", "无视碰撞", null);
            SR.LocDesc("Builder Enhancements", "Collision Override", "无视碰撞：方块无视放置规则，可以放置在任何位置（重叠/空中/交叉）", "Ignore collision: pieces ignore placement rules and can go anywhere (overlap/air/crossing)");
            SR.LocKey("Builder Enhancements", "Collision Toggle Key", "无视碰撞开关", null);
            SR.LocDesc("Builder Enhancements", "Collision Toggle Key", "游戏内切换无视碰撞覆盖（默认 F1）", "Toggle ignore-collision in-game (default F1)");
            SR.LocKey("Builder Enhancements", "Grid Override", "自由放置", null);
            SR.LocDesc("Builder Enhancements", "Grid Override", "自由放置：方块不再吸附 1 单位网格，改为吸附 0.01 单位，可自由微调摆放位置。\n只改放置坐标取整精度，不改变玩法/物理；仅建造阶段生效。\n开启后无需按键即可自由摆放，关闭恢复标准网格吸附。", "Free placement: pieces no longer snap to the 1-unit grid - they snap to 0.01 units, so you can fine-tune placement.\nOnly changes placement rounding precision, not gameplay/physics; build phase only.\nNo key needed while on; turning it off restores the standard grid snap.");
            SR.LocKey("Builder Enhancements", "Grid Toggle Key", "自由放置开关", null);
            SR.LocDesc("Builder Enhancements", "Grid Toggle Key", "游戏内切换自由放置覆盖（默认 F2）", "Toggle free placement in-game (default F2)");
        }

        //「覆盖」条目的同行同伴：它的开关键位（通用条目行按注册的 key 再渲染一个控件）
        private static string BuilderCompanion(ConfigEntryBase e) {
            if (e.Definition.Section != "Builder Enhancements") return null;
            if (e.Definition.Key == "Collision Override") return "Collision Toggle Key";
            if (e.Definition.Key == "Grid Override") return "Grid Toggle Key";
            return null;
        }

        //两个开关键位并进「覆盖」行显示，不单独成行
        private static bool BuilderRowVisible(ConfigEntryBase e) {
            if (e.Definition.Section != "Builder Enhancements") return true;
            return e.Definition.Key != "Collision Toggle Key" && e.Definition.Key != "Grid Toggle Key";
        }

        //本功能的自绘页：先画「建造上限」小分区（分隔标题 + 当前生效值），再逐行渲染本段条目
        private static void RenderPage() {
            float col = Mathf.Max(SR.Ctl.Sc(180), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SR.Ctl.SectionEntries("Builder Enhancements");
            bool any = false, limitDiv = false;
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (!limitDiv && (e.Definition.Key == "Lift Build Cap" || e.Definition.Key == "Build Cap Value")) {
                    limitDiv = true;
                    GUILayout.Space(SR.Ctl.Sc(6));
                    GUILayout.Label(new GUIContent("— " + SR.T("建造上限", "Build Limit") + " —",
                        SR.T("建造上限（BuildUnlimiter）：解除树屋保存/发布界面的关卡满度限制，超满的关卡也能正常发布/上传。",
                             "Build Limit (BuildUnlimiter): lifts the treehouse save/publish fullness cap so over-full levels can be published.")),
                        SR.Ctl.SecHeader);
                    GUILayout.Space(SR.Ctl.Sc(2));
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(new GUIContent(SR.T("当前上限", "Current limit"),
                        SR.T("当前生效的满度上限；游戏原版为 500", "The current effective fullness cap; vanilla is 500")),
                        SR.Ctl.Label, GUILayout.Width(col), GUILayout.Height(SR.Ctl.Sc(26)));
                    GUILayout.FlexibleSpace();
                    bool oldEn = GUI.enabled;
                    GUI.enabled = false;
                    GUILayout.TextField(SR.T("当前上限：" + BuildUnlimiter.CurrentLimit(), "Current: " + BuildUnlimiter.CurrentLimit()),
                        SR.Ctl.SearchBox, GUILayout.Width(SR.Ctl.Sc(170)), GUILayout.Height(SR.Ctl.Sc(26)));
                    GUI.enabled = oldEn;
                    GUILayout.EndHorizontal();
                    GUILayout.Space(SR.Ctl.Sc(2));
                }
                any = true;
                SR.Ctl.DrawEntryRow(e, col);
            }
            if (!any) GUILayout.Label(SR.T("（无匹配条目）", "(no matching entries)"), SR.Ctl.Label);
        }

        public void Initialize(MainPlugin plugin) {
            SelfReg();
            _mp = plugin;
            _collisionEntry = _mp.Config.Bind(
                "Builder Enhancements",
                "Collision Override",
                false,
                "Whether pieces ignore placement rules (can be placed anywhere) at startup");
            _collisionOverride = _collisionEntry.Value;
            _collisionEntry.SettingChanged += (s, e) => _collisionOverride = _collisionEntry.Value;
            _toggleCollisionKey = _mp.Config.Bind(
                "Builder Enhancements",
                "Collision Toggle Key",
                KeyCode.F1,
                "Key to toggle the collision override in-game");
            SR.RegisterKey("建造增强-碰撞开关", _toggleCollisionKey, "toggle");
            _gridEntry = _mp.Config.Bind(
                "Builder Enhancements",
                "Grid Override",
                false,
                "Whether pieces snap to a fine 0.01 grid instead of the 1-unit build grid (free placement)");
            _gridOverride = _gridEntry.Value;
            _gridEntry.SettingChanged += (s, e) => _gridOverride = _gridEntry.Value;
            _toggleGridKey = _mp.Config.Bind(
                "Builder Enhancements",
                "Grid Toggle Key",
                KeyCode.F2,
                "Key to toggle free placement (grid override) in-game");
            SR.RegisterKey("建造增强-自由放置开关", _toggleGridKey, "toggle");

            //分开注册：嵌套类的 CreateAndPatchAll 不递归，需显式注册，单个失败不影响其他。
            try { Harmony.CreateAndPatchAll(typeof(BuilderEnhancements)); } catch (Exception e) { MainPlugin.ModLogger.LogWarning("BuilderEnhancements 补丁失败: " + e.Message); }
            try { Harmony.CreateAndPatchAll(typeof(FreePlacementPatch)); } catch (Exception e) { MainPlugin.ModLogger.LogError("自由放置补丁注册失败: " + e.Message); }
        }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void ToggleKeys() {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            if (SR.ComboKeyDown(_toggleCollisionKey)) { _collisionEntry.Value = !_collisionEntry.Value; Notify(SR.T("无视碰撞", "Collision Override"), _collisionEntry.Value); }
            if (SR.ComboKeyDown(_toggleGridKey)) { _gridEntry.Value = !_gridEntry.Value; Notify(SR.T("自由放置", "Free Placement"), _gridEntry.Value); }
        }

        private static void Notify(string name, bool state) {
            UserMessageManager.Instance.UserMessage(name + (state ? SR.T("开", " ON") : SR.T("关", " OFF")), false);
        }

        [HarmonyPatch(typeof(PiecePlacementCursor), "ReceiveEvent")]
        [HarmonyPostfix]
        static void OnPieceInput(PiecePlacementCursor __instance) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            if (__instance.Piece == null) return;
            __instance.Piece.IgnorePlacementRules = _collisionOverride;
        }

        //自由放置：游戏把 Piece 放到吸附后的 gridPosition 后，用 Postfix 改成未取整的光标位置，
        //实现自由微调摆放（Postfix 比 transpiler 可靠）。
        [HarmonyPatch]
        static class FreePlacementPatch {
            private static FieldInfo _heldOffsetField;
            static IEnumerable<MethodBase> TargetMethods() {
                yield return AccessTools.Method(typeof(PiecePlacementCursor), "SetPiece");
                yield return AccessTools.Method(typeof(PiecePlacementCursor), "FixedUpdate");
            }
            static void Postfix(PiecePlacementCursor __instance) {
                try {
                    if (!GridOverride) return;
                    if (__instance == null || __instance.Piece == null) return;
                    //自由位置 = 光标位置 + 手持偏移 + 变体偏移（不取整）
                    Vector2 offset = __instance.Piece.GetTransformedPlacementOffset();
                    Vector3 cp = __instance.transform.position;
                    if (_heldOffsetField == null)
                        _heldOffsetField = AccessTools.Field(typeof(PiecePlacementCursor), "heldPositionOffset");
                    Vector3 held = Vector3.zero;
                    if (_heldOffsetField != null) {
                        try { held = (Vector3)_heldOffsetField.GetValue(__instance); } catch (Exception __ex) { SR.Guard.Log("BuilderEnhancements.Field", __ex); }
                    }
                    Vector2 freePos = new Vector2(cp.x + held.x + offset.x, cp.y + held.y + offset.y);
                    __instance.Piece.transform.position = new Vector3(freePos.x, freePos.y, __instance.Piece.transform.position.z);
                } catch (Exception __ex) { SR.Guard.Log("BuilderEnhancements.Vector3", __ex); }
            }
        }
    }
}
