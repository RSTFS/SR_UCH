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
        //runtime toggles (also controlled by the in-game manager)
        public static bool Enabled = true;
        public static bool CollisionOverride { get { return _collisionOverride; } set { _collisionOverride = value; } }

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
            ModManager.RegisterKey("建造增强-碰撞开关", _toggleCollisionKey, "toggle");

            Harmony.CreateAndPatchAll(typeof(BuilderEnhancements));
        }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void ToggleKeys() {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (ModManager.UiOpen && ModManager.BlockInput) return; 
            bool collLocked = !Experiments.IsProgressionUnlocked();
            if (collLocked) {
                if (ModManager.ComboKeyDown(_toggleCollisionKey)) Notify(ModManager.T("无视碰撞", "Collision Override") + ModManager.T("（未解锁）", " (locked)"), false);
            } else {
                if (ModManager.ComboKeyDown(_toggleCollisionKey)) { _collisionEntry.Value = !_collisionEntry.Value; Notify(ModManager.T("无视碰撞", "Collision Override"), _collisionEntry.Value); }
            }
        }

        private static void Notify(string name, bool state) {
            UserMessageManager.Instance.UserMessage(name + (state ? ModManager.T("开", " ON") : ModManager.T("关", " OFF")), false);
        }

        //collision override（无视碰撞规则）；A 组进度未解锁时即使开关被 cfg/外部打开也不生效
        [HarmonyPatch(typeof(PiecePlacementCursor), "ReceiveEvent")]
        [HarmonyPostfix]
        static void OnPieceInput(PiecePlacementCursor __instance) {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (ModManager.UiOpen && ModManager.BlockInput) return; 
            if (__instance.Piece == null) return;
            __instance.Piece.IgnorePlacementRules = _collisionOverride && Experiments.IsProgressionUnlocked();
        }

        //remove rotation clamping when restoring saved pieces
        [HarmonyPatch(typeof(QuickSaver), "RestoreSaveables")]
        static class RestoreSaveablesPatch {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                MethodInfo target = AccessTools.PropertySetter(typeof(Transform), "rotation");
                List<CodeInstruction> list = new List<CodeInstruction>(instructions);
                int a = list.FindIndex(i => i.opcode == OpCodes.Callvirt && i.operand != null && i.operand.Equals(target));
                int b = list.FindLastIndex(i => i.opcode == OpCodes.Stfld);
                int c = list.FindLastIndex(i => i.opcode == OpCodes.Stloc_S) + 1;
                if (a >= 0 && c > a) list.RemoveRange(a, c - a);
                a = list.FindIndex(i => i.opcode == OpCodes.Callvirt && i.operand != null && i.operand.Equals(target));
                b = list.FindLastIndex(i => i.opcode == OpCodes.Stfld);
                c = list.FindLastIndex(i => i.opcode == OpCodes.Stloc_S) + 1;
                if (a >= 0 && c > a) list.RemoveRange(a, c - a);
                return list;
            }
        }
    }
}
