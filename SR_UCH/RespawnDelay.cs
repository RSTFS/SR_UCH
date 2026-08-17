using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //custom respawn delay: after death the player respawns after a configurable delay.
    //the game's default auto-respawn is suppressed so the configured delay is the only one.
    public class RespawnDelay : ITweak {
        private static MainPlugin _mp;
        private static ConfigEntry<float> _delay;
        //runtime toggle (also controlled by the in-game manager)
        public static bool Enabled = true;

        private static readonly HashSet<Character> _pending = new HashSet<Character>();
        private static MethodInfo _reset;
        private static bool _resetResolved;

        private class DelayComponent : MonoBehaviour {
            public void Schedule(Character c) {
                StartCoroutine(Accelerate(c));
            }

            private IEnumerator Accelerate(Character c) {
                float t = 0f;
                float delay = Mathf.Max(0.1f, _delay.Value);
                while (t < delay) {
                    t += Time.deltaTime;
                    yield return null;
                }
                _pending.Remove(c);
                if (c == null || _reset == null) yield break;
                FreePlayControl control = LobbyManager.instance != null
                    ? LobbyManager.instance.CurrentGameController as FreePlayControl
                    : null;
                if (control == null) yield break;
                try {
                    _reset.Invoke(control, new object[] { c, true });
                } catch (Exception e) {
                    MainPlugin.ModLogger.LogWarning("RespawnDelay: resetPlayerCharacter failed: " + e.Message);
                }
            }
        }

        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            _delay = _mp.Config.Bind(
                "Respawn",
                "Delay",
                1.0f,
                "How long (in seconds) after death before the player respawns (minimum 0.1)");
            GameObject go = new GameObject("SR_UCHRespawnDelay");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<DelayComponent>();
            Harmony.CreateAndPatchAll(typeof(RespawnDelay));
        }

        //death hook: schedule the respawn for the local player
        [HarmonyPatch(typeof(Character), "setupDeath")]
        [HarmonyPostfix]
        static void OnDeath(Character __instance) {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (!ModManager.IgnoreModeLimit && GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY) return;
            if (__instance == null || !__instance.hasAuthority) return;
            if (__instance.Success) return;
            if (_pending.Contains(__instance)) return;

            if (!_resetResolved) {
                _resetResolved = true;
                _reset = AccessTools.Method(typeof(FreePlayControl), "resetPlayerCharacter", new[] { typeof(Character), typeof(bool) });
                if (_reset == null)
                    MainPlugin.ModLogger.LogWarning("RespawnDelay: resetPlayerCharacter not found — respawn delay disabled.");
            }
            if (_reset == null) return;

            _pending.Add(__instance);
            DelayComponent comp = UnityEngine.Object.FindObjectOfType<DelayComponent>();
            if (comp != null) comp.Schedule(__instance);
        }

        //suppress the game's default auto-respawn in FreePlayControl.Update so the
        //configured delay is the only thing that respawns the player. The transpiler injects
        //this field instead of a constant so it can be toggled at runtime: when the mod (or
        //its master switch) is off the game's normal auto-respawn is restored.
        private static int _suppress = -1;
        internal static int SuppressValue {
            get { return (ModManager.AllEnabled && Enabled) ? -1 : int.MaxValue; }
        }

        [HarmonyPatch(typeof(FreePlayControl), "Update")]
        [HarmonyPrefix]
        static void UpdateSuppress() {
            _suppress = SuppressValue;
        }

        [HarmonyPatch(typeof(FreePlayControl), "Update")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ForceNoReset(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            for (int i = 1; i < list.Count; i++) {
                if (list[i].opcode == OpCodes.Ldc_I4_1 && list[i - 1].ToString().Contains("get_Count")) {
                    list[i] = new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RespawnDelay), "_suppress"));
                }
            }
            return list;
        }
    }
}
