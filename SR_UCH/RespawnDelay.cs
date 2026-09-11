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
                if (c == null) yield break;
                GameState.GameMode gm = GameSettings.GetInstance().GameMode;
                //挑战模式：不触发自动重试（自动重试归「快速重试」分区管理）。
                //重生延迟在挑战模式只用于延迟等待（不影响其他行为）。
                if (gm == GameState.GameMode.CHALLENGE) {
                    yield break;
                }
                //自由模式：延迟结束后原地复活
                if (_reset == null) yield break;
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

        //death hook: schedule the respawn for the local player (仅自由模式；挑战模式重试逻辑移到「快速重试」分区)
        [HarmonyPatch(typeof(Character), "setupDeath")]
        [HarmonyPostfix]
        static void OnDeath(Character __instance) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            GameState.GameMode gm = GameSettings.GetInstance().GameMode;
            if (!SR.GateModeAllows(SR.ModeMask.Freeplay | SR.ModeMask.Challenge)) return;
            if (__instance == null || !__instance.hasAuthority) return;
            if (__instance.Success) return;
            if (_pending.Contains(__instance)) return;

            //挑战模式：重生延迟只用于等待，不触发动作（自动重试归「快速重试」分区）
            if (gm == GameState.GameMode.CHALLENGE) return;

            if (!_resetResolved) {
                _resetResolved = true;
                _reset = AccessTools.Method(typeof(FreePlayControl), "resetPlayerCharacter", new[] { typeof(Character), typeof(bool) });
                if (_reset == null)
                    MainPlugin.ModLogger.LogWarning("RespawnDelay: resetPlayerCharacter not found — respawn delay disabled.");
            }
            if (_reset == null) return;

            _pending.Add(__instance);
            DelayComponent comp2 = UnityEngine.Object.FindObjectOfType<DelayComponent>();
            if (comp2 != null) comp2.Schedule(__instance);
        }

        //suppress the game's default auto-respawn in FreePlayControl.Update so the
        //configured delay is the only thing that respawns the player. The transpiler injects
        //this field instead of a constant so it can be toggled at runtime: when the mod (or
        //its master switch) is off the game's normal auto-respawn is restored.
        private static int _suppress = -1;
        internal static int SuppressValue {
            get { return (SR.GateMaster && Enabled) ? -1 : int.MaxValue; }
        }

        [HarmonyPatch(typeof(FreePlayControl), "Update")]
        [HarmonyPrefix]
        static void UpdateSuppress() {
            _suppress = SuppressValue;
        }

        [HarmonyPatch(typeof(FreePlayControl), "Update")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ForceNoReset(IEnumerable<CodeInstruction> instructions) {
            //原来用 list[i-1].ToString().Contains("get_Count") 匹配 IL——字符串包含判断在游戏
            //更新后会**静默失效**（提示词第 7 节“Transpiler 脆弱”）。改为按操作数 MethodInfo
            //精确匹配 get_Count，并在匹配失败时打日志告警 + 原样返回（降级），不再悄悄失效。
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            bool matched = false;
            for (int i = 1; i < list.Count; i++) {
                if (list[i].opcode != OpCodes.Ldc_I4_1) continue;
                MethodInfo prev = list[i - 1].operand as MethodInfo;
                if (prev == null || prev.Name != "get_Count") continue;
                list[i] = new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RespawnDelay), "_suppress"));
                matched = true;
            }
            if (!matched) {
                MainPlugin.ModLogger.LogWarning("RespawnDelay: 未匹配到 FreePlayControl.Update 的 get_Count 模式，重生延迟抑制降级为不生效（游戏可能已更新，需重新适配）");
            }
            return list;
        }
    }
}
