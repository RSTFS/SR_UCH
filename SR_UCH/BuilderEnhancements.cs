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

        public void Initialize(MainPlugin plugin) {
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
            //自由放置：关闭标准网格吸附（1 单位 → 0.01 单位），可自由微调摆放
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

            //注册补丁。用 try/catch 分开注册，单个失败不影响其他（嵌套类 CreateAndPatchAll 不递归，需显式注册）。
            try { Harmony.CreateAndPatchAll(typeof(BuilderEnhancements)); } catch (Exception e) { MainPlugin.ModLogger.LogWarning("BuilderEnhancements 补丁失败: " + e.Message); }
            try { Harmony.CreateAndPatchAll(typeof(FreePlacementPatch)); } catch (Exception e) { MainPlugin.ModLogger.LogError("自由放置补丁注册失败: " + e.Message); }
        }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void ToggleKeys() {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            //进度锁走统一门控（能力门，读每帧快照缓存；不再每帧直读存档）
            bool collLocked = !SR.ProgressionAllows(SR_UCH.Gating.ProgressGroup.A);
            if (collLocked) {
                //A 组未解锁时按键静默忽略（不再弹“未解锁”提示）
                SR.ComboKeyDown(_toggleCollisionKey);
            } else {
                if (SR.ComboKeyDown(_toggleCollisionKey)) { _collisionEntry.Value = !_collisionEntry.Value; Notify(SR.T("无视碰撞", "Collision Override"), _collisionEntry.Value); }
            }
            //自由放置（网格覆盖）：A 组解锁（同无视碰撞）
            if (collLocked) {
                SR.ComboKeyDown(_toggleGridKey); //A 组未解锁：按键静默忽略
            } else {
                if (SR.ComboKeyDown(_toggleGridKey)) { _gridEntry.Value = !_gridEntry.Value; Notify(SR.T("自由放置", "Free Placement"), _gridEntry.Value); }
            }
        }

        private static void Notify(string name, bool state) {
            UserMessageManager.Instance.UserMessage(name + (state ? SR.T("开", " ON") : SR.T("关", " OFF")), false);
        }

        //collision override（无视碰撞规则）；A 组进度未解锁时即使开关被 cfg/外部打开也不生效
        [HarmonyPatch(typeof(PiecePlacementCursor), "ReceiveEvent")]
        [HarmonyPostfix]
        static void OnPieceInput(PiecePlacementCursor __instance) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            if (__instance.Piece == null) return;
            __instance.Piece.IgnorePlacementRules = _collisionOverride && SR.ProgressionAllows(SR_UCH.Gating.ProgressGroup.A);
        }

        //自由放置（网格覆盖）：在游戏计算网格位置后，把 Piece 位置改为自由位置（不吸附 1 单位网格）。
        //用 Postfix 而非 transpiler（更可靠）：游戏 FixedUpdate/SetPiece 把 Piece 放到吸附后的
        //gridPosition，Postfix 再改成未取整的光标位置，实现自由微调摆放。
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
