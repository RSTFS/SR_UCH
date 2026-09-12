using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
// 重生（一个类 / 一个 section / 一次补丁注册）：配置 + 补丁 + 逻辑 + 文案都在本文件。
// 合并三块功能时的命名：出生无敌与死亡延迟本来就共用同一个 cfg 项（"Respawn"/"Enabled"）→ 只留一个 `Enabled`；
// 重生点自己的开关叫 `SpawnPointsEnabled`；`Respawn()` 与类名同名会被编译器当构造函数 → 改名 `RespawnLocal()`。
public class Respawn : ITweak {
    private static MainPlugin _mp;

    //重生组总开关（出生无敌 + 死亡延迟共用；原 NoSpawnImmunity/RespawnDelay 各自的 Enabled 合成一个）
    public static bool Enabled = true;

    //--- 子功能①：自定义重生点（原 SpawnPoints）---
    public static bool SpawnPointsEnabled = false;
    public static readonly List<Vector3> CustomPoints = new List<Vector3>();
    public static Vector3? DefaultPoint;
    private static ConfigEntry<bool> _enabledEntry;
    private static ConfigEntry<KeyCode> _setKey;
    private static ConfigEntry<KeyCode> _respawnKey;
    private static ConfigEntry<KeyCode> _resetKey;
    private static MethodInfo _getSpawnPos;

    //--- 子功能②：出生无敌时长（原 NoSpawnImmunity）---
    private static ConfigEntry<float> _immunityTime;

    //--- 子功能③：死亡后延迟重生（原 RespawnDelay）---
    private static ConfigEntry<float> _delay;
    private static readonly HashSet<Character> _pending = new HashSet<Character>();
    private static MethodInfo _reset;
    private static bool _resetResolved;

    //本文件功能的界面文案（自包含；原来集中在 SR.Localization.cs 的表里）
    private static void SelfReg() {
        SR.LocSec("Respawn", "重生", null);
        SR.NavHide("Respawn"); //T7：重生条目并入「自由模式」页渲染，不单独成侧栏栏目
        SR.LocKey("Respawn", "Spawn Immunity", "重生无敌时间", null);
        SR.LocDesc("Respawn", "Spawn Immunity", "重生后的无敌时长（秒）：复活后短时间内免疫伤害（仅自由模式）", "Invincibility seconds after respawn (freeplay only)");
        SR.LocKey("Respawn", "Delay", "重生延迟", null);
        SR.LocDesc("Respawn", "Delay", "死亡后多少秒重生（最小 0.1 秒）：延迟后原地复活回出生点（仅自由模式）。\n挑战模式的重试等待请在「快速调整」页的「快速重试」分区设置。", "Seconds before respawn after death (min 0.1): respawns at the start point after the delay (freeplay only).\nFor the Challenge retry hold-time, see the Quick Retry section on the Quick Adjust page.");
        SR.LocKey("Respawn", "Enabled", "重生功能总开关", null);
        SR.LocDesc("Respawn", "Enabled", "重生功能总开关：重生无敌时间 + 重生延迟（仅自由模式）", "Master switch: respawn invincibility + respawn delay (freeplay only)");
        SR.LocKey("Respawn", "Set Spawn Key", "设置重生点键", null);
        SR.LocDesc("Respawn", "Set Spawn Key", "在当前位置设置一个自定义重生点（默认 O）（仅自由模式）", "Set a custom spawn point at your position (default O) (freeplay only)");
        SR.LocKey("Respawn", "Respawn Key", "重生键", null);
        SR.LocDesc("Respawn", "Respawn Key", "传送到最近的自定义重生点，没有则去游戏默认起点（默认 P）（仅自由模式）", "Teleport to the nearest custom spawn, else the game default start (default P) (freeplay only)");
        SR.LocKey("Respawn", "Reset Spawn Keys", "恢复重生点键", null);
        SR.LocDesc("Respawn", "Reset Spawn Keys", "删除所有自定义重生点，保留游戏默认起点（默认 K）（仅自由模式）", "Remove all custom spawn points, keep the game default (default K) (freeplay only)");
        SR.LocKey("Respawn", "Spawn Points Enabled", "重生点总开关", null);
        SR.LocDesc("Respawn", "Spawn Points Enabled", "重生点功能总开关：设置重生点 / 瞬移重生 / 恢复重生点（仅自由模式）", "Master switch: set spawn / teleport respawn / reset spawn (freeplay only)");
    }

    public void Initialize(MainPlugin plugin) {
        SelfReg();
        _mp = plugin;
        //重生组总开关（三个子功能共用这一个配置项，保持与旧版 cfg 完全兼容）
        ConfigEntry<bool> enabled = _mp.Config.Bind("Respawn", "Enabled", false, "重生功能总开关（仅自由模式可用）");
        Enabled = enabled.Value;
        enabled.SettingChanged += (s, e) => Enabled = enabled.Value;

        //① 自定义重生点
        _enabledEntry = _mp.Config.Bind("Respawn", "Spawn Points Enabled", false, "重生点功能总开关（设置重生点 / 重生 / 恢复重生点，仅自由模式可用）");
        SpawnPointsEnabled = _enabledEntry.Value;
        _enabledEntry.SettingChanged += (s, e) => SpawnPointsEnabled = _enabledEntry.Value;
        _setKey = _mp.Config.Bind("Respawn", "Set Spawn Key", KeyCode.O, "在当前位置设置重生点（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
        _respawnKey = _mp.Config.Bind("Respawn", "Respawn Key", KeyCode.P, "重生（传送到最近的自定义重生点，无则游戏默认；支持组合键）");
        _resetKey = _mp.Config.Bind("Respawn", "Reset Spawn Keys", KeyCode.K, "恢复重生点（删除所有自定义重生点，保留游戏默认；支持组合键）");
        SR.RegisterKey("重生点-设置", _setKey, "press");
        SR.RegisterKey("重生点-重生", _respawnKey, "press");
        SR.RegisterKey("重生点-恢复", _resetKey, "press");
        SceneManager.activeSceneChanged += (a, b) => { DefaultPoint = null; };

        //② 出生无敌时长
        _immunityTime = _mp.Config.Bind(
            "Respawn",
            "Spawn Immunity",
            0.3f,
            "The altered spawn immunity");

        //③ 死亡后延迟重生
        _delay = _mp.Config.Bind(
            "Respawn",
            "Delay",
            1.0f,
            "How long (in seconds) after death before the player respawns (minimum 0.1)");
        GameObject go = new GameObject("SR_UCHRespawn");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<DelayComponent>();

        //三组补丁一次注册（原来三个类各自 CreateAndPatchAll）
        Harmony.CreateAndPatchAll(typeof(Respawn), (string)null);
    }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void SpawnKeys() {
            if (!SR.GateMaster) return;
            if (!SpawnPointsEnabled) return;
            if (SR.UiOpen && SR.BlockInput) return;
            if (SR.MapOpen) return; //地图打开时 O/P/K 由地图页面处理，避免重复设置
            //重生点功能只在自由模式可用（统一门控；IgnoreModeLimit 已豁免）
            if (!SR.GateModeAllows(SR.ModeMask.Freeplay)) return;
            if (SR.ComboKeyDown(_setKey)) SetPoint(GetLocalPosition());
            if (SR.ComboKeyDown(_respawnKey)) RespawnLocal();
            if (SR.ComboKeyDown(_resetKey)) ResetPoints();
        }

        public static Vector2 GetLocalPosition() {
            //地图界面：优先光标位置（本地玩家的光标；地图上设点/传送以光标为准）
            if (SR.MapOpen) {
                try {
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.AssociatedLobbyPlayer == null) continue;
                        if (!p.AssociatedLobbyPlayer.IsLocalPlayer) continue;
                        if (p.AssociatedLobbyPlayer.CursorInstance != null)
                            return (Vector2)p.AssociatedLobbyPlayer.CursorInstance.transform.position;
                    }
                } catch (Exception __ex) { SR.Guard.Log("取本地光标位置(Player)", __ex); }
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null && lm.PlayerTracker != null) {
                        for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                            LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(lm.PlayerTracker.GetPlayerInfoByIndex(i).NetworkNumber);
                            if (lp == null || !lp.IsLocalPlayer) continue;
                            if (lp.CursorInstance != null) return (Vector2)lp.CursorInstance.transform.position;
                        }
                    }
                } catch (Exception __ex) { SR.Guard.Log("取本地光标位置(LobbyPlayer)", __ex); }
            }
            //不在地图界面：优先本地玩家的实际角色（Player.PlayerCharacter / GamePlayer.CharacterInstance）；
            //LobbyPlayer.CharacterInstance 对局中可能为 null，直接用它会落到光标位置 → 点不对
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null || p.AssociatedLobbyPlayer == null) continue;
                    if (!p.AssociatedLobbyPlayer.IsLocalPlayer) continue;
                    Character ch = p.PlayerCharacter;
                    if (ch != null) return (Vector2)ch.transform.position;
                    if (p.AssociatedGamePlayer != null && p.AssociatedGamePlayer.CharacterInstance != null)
                        return (Vector2)p.AssociatedGamePlayer.CharacterInstance.transform.position;
                    if (p.AssociatedLobbyPlayer.CursorInstance != null)
                        return (Vector2)p.AssociatedLobbyPlayer.CursorInstance.transform.position;
                    return Vector2.zero;
                }
            } catch (Exception __ex) { SR.Guard.Log("取本地角色位置(Player)", __ex); }
            //树屋/大厅：优先"选中的角色"（选中后光标会隐藏，位置以角色为准）
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                        LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(lm.PlayerTracker.GetPlayerInfoByIndex(i).NetworkNumber);
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        if (lp.CharacterInstance != null) return (Vector2)lp.CharacterInstance.transform.position;
                        if (lp.CursorInstance != null) return (Vector2)lp.CursorInstance.transform.position;
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("取本地角色位置(树屋)", __ex); }
            foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                if (c != null && c.hasAuthority) return c.transform.position;
            }
            return Vector2.zero;
        }

        //传送本地玩家到指定位置：对局中传 Character（光标在行动状态是隐藏的），树屋用 LobbyCursor（CursorInstance）
        public static void TeleportLocalPlayer(Vector2 world) {
            //对局中（自由模式等）：直接传送本地角色——行动状态时光标被隐藏，传光标没用
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentGameController != null) {
                    foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                        if (c == null || !c.hasAuthority) continue;
                        Rigidbody2D rb = c.GetComponent<Rigidbody2D>();
                        if (rb != null) rb.position = world;
                        Vector3 p = c.transform.position;
                        c.transform.position = new Vector3(world.x, world.y, p.z);
                        return;
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("传送本地角色(对局)", __ex); }
            //树屋/大厅：优先传送“选中的角色”（选中角色后光标会被隐藏，玩家的存在感 = 角色本体）
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                        LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(lm.PlayerTracker.GetPlayerInfoByIndex(i).NetworkNumber);
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        //选中角色：同时移动角色本体 + 光标（光标保持控制点一致）
                        Character ch = lp.CharacterInstance;
                        if (ch != null) {
                            Rigidbody2D rb = ch.GetComponent<Rigidbody2D>();
                            if (rb != null) rb.position = world;
                            Vector3 p = ch.transform.position;
                            ch.transform.position = new Vector3(world.x, world.y, p.z);
                        }
                        if (lp.CursorInstance != null) {
                            lp.CursorInstance.transform.position = new Vector3(world.x, world.y, lp.CursorInstance.transform.position.z);
                        } else {
                            //CursorInstance 未赋值时：按本地玩家关联找 LobbyCursor（树屋光标）
                            foreach (LobbyCursor lc in UnityEngine.Object.FindObjectsOfType<LobbyCursor>()) {
                                if (lc == null) continue;
                                if (lc.AssociatedLobbyPlayer == lp || lc.networkNumber == lp.networkNumber) {
                                    lc.transform.position = new Vector3(world.x, world.y, lc.transform.position.z);
                                    break;
                                }
                            }
                        }
                        return;
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("传送本地角色(树屋)", __ex); }
            //最后兜底：Character
            foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                if (c == null || !c.hasAuthority) continue;
                Rigidbody2D rb = c.GetComponent<Rigidbody2D>();
                if (rb != null) rb.position = world;
                Vector3 p = c.transform.position;
                c.transform.position = new Vector3(world.x, world.y, p.z);
                break;
            }
        }

        public static void SetPoint(Vector2 pos) {
            CustomPoints.Add(pos);
            if (CustomPoints.Count > 20) CustomPoints.RemoveAt(0);
        }

        public static void ResetPoints() {
            CustomPoints.Clear();
        }

        //删除鼠标位置附近的自定义重生点（最接近的）；游戏默认起点不在列表里，天然不可删。
        public static bool RemoveNearest(Vector2 world, float maxDist) {
            int best = -1;
            float bestD = maxDist * maxDist;
            for (int i = 0; i < CustomPoints.Count; i++) {
                float d = ((Vector2)CustomPoints[i] - world).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = i; }
            }
            if (best >= 0) {
                CustomPoints.RemoveAt(best);
                return true;
            }
            return false;
        }

        //read (and cache) the game's default spawn position for the current level
        public static void ReadDefaultSpawn() {
            if (DefaultPoint.HasValue) return;
            try {
                if (_getSpawnPos == null)
                    _getSpawnPos = AccessTools.Method(typeof(Level), "GetSpawnPosition", new[] { typeof(float) });
                Level lv = UnityEngine.Object.FindObjectOfType<Level>();
                if (lv != null && _getSpawnPos != null) {
                    DefaultPoint = (Vector3)_getSpawnPos.Invoke(lv, new object[] { 0f });
                }
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("Respawn: GetSpawnPosition failed: " + ex.Message);
            }
        }

        public static void RespawnLocal() {
            Vector3 target;
            if (CustomPoints.Count > 0) {
                Vector2 local = GetLocalPosition();
                int best = 0;
                float bestD = float.MaxValue;
                for (int i = 0; i < CustomPoints.Count; i++) {
                    float d = Vector2.Distance(local, CustomPoints[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
                target = CustomPoints[best];
            } else {
                ReadDefaultSpawn();
                if (!DefaultPoint.HasValue) return;
                target = DefaultPoint.Value;
            }
            TeleportLocalPlayer(target);
        }
        [HarmonyPatch(typeof(Character), "StartInvincibleTimer")]
        [HarmonyPrefix]
        static void ImmunityPatch(ref float time) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            //仅自由模式生效（统一门控；IgnoreModeLimit 已豁免）
            if (SR.GateModeAllows(SR.ModeMask.Freeplay)) {
                time = _immunityTime.Value;
            }
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
                    MainPlugin.ModLogger.LogWarning("Respawn: resetPlayerCharacter not found — respawn delay disabled.");
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
            //精确匹配 get_Count，并在匹配失败时打日志告警 + 原样返回（降级），不再悄悄失效。
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            bool matched = false;
            for (int i = 1; i < list.Count; i++) {
                if (list[i].opcode != OpCodes.Ldc_I4_1) continue;
                MethodInfo prev = list[i - 1].operand as MethodInfo;
                if (prev == null || prev.Name != "get_Count") continue;
                list[i] = new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Respawn), "_suppress"));
                matched = true;
            }
            if (!matched) {
                MainPlugin.ModLogger.LogWarning("Respawn: 未匹配到 FreePlayControl.Update 的 get_Count 模式，重生延迟抑制降级为不生效（游戏可能已更新，需重新适配）");
            }
            return list;
        }

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
                    MainPlugin.ModLogger.LogWarning("Respawn: resetPlayerCharacter failed: " + e.Message);
                }
            }
        }

}
}
