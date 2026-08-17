using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    //custom spawn points (BetterFreeplay-style):
    //  O = set a spawn point at the current position
    //  P = respawn (teleport to the nearest custom point, else the game default)
    //  K = reset spawn points (remove all custom ones, keep the game default)
    //the game's default spawn point is read from Level.GetSpawnPosition and never deletable.
    public class SpawnPoints : ITweak {
        public static bool Enabled = true;
        public static readonly List<Vector3> CustomPoints = new List<Vector3>();
        public static Vector3? DefaultPoint;
        private static ConfigEntry<bool> _enabledEntry;
        private static ConfigEntry<KeyCode> _setKey;
        private static ConfigEntry<KeyCode> _respawnKey;
        private static ConfigEntry<KeyCode> _resetKey;
        private static MethodInfo _getSpawnPos;

        public void Initialize(MainPlugin plugin) {
            _enabledEntry = plugin.Config.Bind("Respawn", "Spawn Points Enabled", false, "重生点功能总开关（设置重生点 / 重生 / 恢复重生点，仅自由模式可用）");
            Enabled = _enabledEntry.Value;
            _enabledEntry.SettingChanged += (s, e) => Enabled = _enabledEntry.Value;
            _setKey = plugin.Config.Bind("Respawn", "Set Spawn Key", KeyCode.O, "在当前位置设置重生点（组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
            _respawnKey = plugin.Config.Bind("Respawn", "Respawn Key", KeyCode.P, "重生（传送到最近的自定义重生点，无则游戏默认；支持组合键）");
            _resetKey = plugin.Config.Bind("Respawn", "Reset Spawn Keys", KeyCode.K, "恢复重生点（删除所有自定义重生点，保留游戏默认；支持组合键）");
            ModManager.RegisterKey("重生点-设置", _setKey, "press");
            ModManager.RegisterKey("重生点-重生", _respawnKey, "press");
            ModManager.RegisterKey("重生点-恢复", _resetKey, "press");
            SceneManager.activeSceneChanged += (a, b) => { DefaultPoint = null; };
            Harmony.CreateAndPatchAll(typeof(SpawnPoints));
        }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void SpawnKeys() {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (ModManager.UiOpen && ModManager.BlockInput) return;
            if (ModManager.MapOpen) return; //地图打开时 O/P/K 由地图页面处理，避免重复设置
            //重生点功能只在自由模式可用（EX"无视模式限制"开启后任何模式都可用）
            if (!ModManager.IgnoreModeLimit && GameSettings.GetInstance().GameMode != GameState.GameMode.FREEPLAY) return;
            if (ModManager.ComboKeyDown(_setKey)) SetPoint(GetLocalPosition());
            if (ModManager.ComboKeyDown(_respawnKey)) Respawn();
            if (ModManager.ComboKeyDown(_resetKey)) ResetPoints();
        }

        public static Vector2 GetLocalPosition() {
            //树屋：优先“选中的角色”（选中后光标会隐藏，位置以角色为准）；对局：Character
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
            } catch { }
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
            } catch { }
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
            } catch { }
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
                MainPlugin.ModLogger.LogWarning("SpawnPoints: GetSpawnPosition failed: " + ex.Message);
            }
        }

        public static void Respawn() {
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
    }
}
