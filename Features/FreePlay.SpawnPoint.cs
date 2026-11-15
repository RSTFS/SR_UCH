// 自由模式 · 重生点：自定义重生点（添加 / 传送 / 恢复游戏默认），配合地图页的右键菜单使用。
using System;
using System.Collections.Generic;
using System.Reflection;
using SR_UCH.Tweaks;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    internal static class SpawnPointTools {
        private const string Sec = "自由模式";
        private const int MaxPoints = 20;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _respawnKey, _addKey, _clearKey;
        private static readonly List<Vector3> _points = new List<Vector3>();
        private static Vector3? _defaultPoint;
        private static MethodInfo _getSpawnPos;

        internal static bool Enabled { get { return _enabled != null && _enabled.Value; } }
        internal static int Count { get { return _points.Count; } }
        internal static IList<Vector3> All { get { return _points; } }
        internal static Vector3 At(int i) { return (i >= 0 && i < _points.Count) ? _points[i] : Vector3.zero; }

        // 离本机玩家最近的重生点下标（无点返回 -1）。
        internal static int NearestIndex() {
            try {
                if (_points.Count == 0) return -1;
                Vector2 me = LocalPosition();
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < _points.Count; i++) {
                    float d = Vector2.Distance(me, _points[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
                return best;
            } catch (Exception __ni) { SRApi.Guard.Log("重生点/最近下标", __ni); return -1; }
        }

        // ---------- 持久化：按"场景名"存每组重生点（换关卡各自保留，重进同一关卡恢复） ----------
        private static ConfigEntry<string> _store;
        private static string _curScene = "";

        private static void SavePoints() {
            try {
                if (_store == null) return;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < _points.Count; i++) {
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(_points[i].x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                      .Append(_points[i].y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                _store.Value = _curScene + "|" + sb;
                SRApi.Ctl.MarkConfigDirty(_store.ConfigFile);
            } catch (Exception __sp) { SRApi.Guard.Log("重生点/保存", __sp); }
        }

        private static void LoadPoints() {
            try {
                _points.Clear();
                _curScene = CurrentScene();
                if (_store == null || string.IsNullOrEmpty(_store.Value)) return;
                string raw = _store.Value;
                int bar = raw.IndexOf('|');
                if (bar < 0) return;
                if (raw.Substring(0, bar) != _curScene) return;   // 不同关卡：不恢复
                string body = raw.Substring(bar + 1);
                if (body.Length == 0) return;
                string[] parts = body.Split(';');
                for (int i = 0; i < parts.Length && _points.Count < MaxPoints; i++) {
                    string[] xy = parts[i].Split(',');
                    if (xy.Length != 2) continue;
                    float x, y;
                    if (!float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) continue;
                    if (!float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)) continue;
                    _points.Add(new Vector3(x, y, 0f));
                }
            } catch (Exception __lp) { SRApi.Guard.Log("重生点/读取", __lp); }
        }

        private static string CurrentScene() {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (Exception) { return ""; }
        }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                _enabled = plugin.Config.Bind(Sec, "Spawn Points", false,
                    "自定义重生点：在地图页右键添加/传送/删除，仅自由模式可用。");
                SRApi.LocKey(Sec, "Spawn Points", "自定义重生点", "Custom spawn points");
                SRApi.LocDesc(Sec, "Spawn Points",
                    "自定义重生点\n地图页右键可添加、传送、删除重生点\n最多 20 个；仅自由模式可用",
                    "custom spawn points\nright-click on the map to add, teleport or remove\nup to 20; freeplay only");
                _store = plugin.Config.Bind(Sec, "Spawn Points Store", "",
                    "内部状态：自定义重生点（按关卡保存，格式 场景名|x,y;x,y）。");
                SRApi.LocKey(Sec, "Spawn Points Store", "重生点数据", "Spawn point data");
                SRApi.LocDesc(Sec, "Spawn Points Store",
                    "内部状态：自定义重生点数据\n按关卡分别保存\n由地图页右键菜单自动维护",
                    "internal: custom spawn point data\nstored per level\nmaintained by the map right-click menu");
                _respawnKey = plugin.Config.Bind(Sec, "Spawn Respawn Key", KeyCode.P,
                    "重生快捷键（默认 P）：传送到最近的自定义重生点，没有则回游戏默认出生点。");
                _addKey = plugin.Config.Bind(Sec, "Spawn Add Key", KeyCode.O,
                    "添加重生点快捷键（默认 O）：在当前位置添加一个自定义重生点。");
                _clearKey = plugin.Config.Bind(Sec, "Spawn Clear Key", KeyCode.K,
                    "清空重生点快捷键（默认 K）：删除全部自定义重生点（保留游戏默认）。");
                SRApi.LocKey(Sec, "Spawn Respawn Key", "重生快捷键", "Respawn hotkey");
                SRApi.LocDesc(Sec, "Spawn Respawn Key",
                    "重生快捷键\n传送到最近的自定义重生点\n没有自定义点时回游戏默认出生点",
                    "respawn hotkey\nteleports to the nearest custom spawn point\nfalls back to the game default");
                SRApi.LocKey(Sec, "Spawn Add Key", "添加重生点快捷键", "Add spawn hotkey");
                SRApi.LocDesc(Sec, "Spawn Add Key",
                    "添加重生点快捷键\n在当前位置添加一个自定义重生点\n最多 20 个",
                    "add spawn point hotkey\nadds a custom point at your position\nup to 20");
                SRApi.LocKey(Sec, "Spawn Clear Key", "清空重生点快捷键", "Clear spawns hotkey");
                SRApi.LocDesc(Sec, "Spawn Clear Key",
                    "清空重生点快捷键\n删除全部自定义重生点\n游戏默认出生点不受影响",
                    "clear spawn points hotkey\nremoves every custom point\nthe game default is untouched");
                SRApi.RegisterKey("重生点-重生", _respawnKey, "press", delegate { if (SRApi.GateMaster && Enabled) Respawn(); });
                SRApi.RegisterKey("重生点-添加", _addKey, "press", delegate { if (SRApi.GateMaster && Enabled) { Add(LocalPosition()); SRApi.Notify(SRApi.T("已添加重生点（", "Spawn point added (") + Count + ")"); } });
                SRApi.RegisterKey("重生点-清空", _clearKey, "press", delegate { if (SRApi.GateMaster && Enabled) { Clear(); SRApi.Notify(SRApi.T("已清空重生点", "Spawn points cleared")); } });
                SRApi.RegisterSceneHook(delegate { _defaultPoint = null; LoadPoints(); });
                LoadPoints();
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/初始化", __ex); }
        }

        internal static void Add(Vector2 world) {
            try {
                if (_enabled != null && !_enabled.Value) return;
                if (_points.Count >= MaxPoints) _points.RemoveAt(0);
                if (_curScene.Length == 0) _curScene = CurrentScene();
                _points.Add(new Vector3(world.x, world.y, 0f));
                SavePoints();
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/添加", __ex); }
        }

        internal static void Clear() {
            try { _points.Clear(); SavePoints(); } catch (Exception __ex) { SRApi.Guard.Log("重生点/清空", __ex); }
        }

        // 删除离本机玩家最近的重生点。
        internal static void RemoveNearest() {
            try {
                if (_points.Count == 0) return;
                Vector2 me = LocalPosition();
                int best = -1; float bestD = float.MaxValue;
                for (int i = 0; i < _points.Count; i++) {
                    float d = Vector2.Distance(me, _points[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best >= 0) { _points.RemoveAt(best); SavePoints(); }
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/删除最近", __ex); }
        }

        internal static Vector2 LocalPosition() {
            try {
                List<Character> all = Character.AllCharacters;
                if (all != null) {
                    for (int i = 0; i < all.Count; i++) {
                        Character c = all[i];
                        if (c != null && c.hasAuthority) return c.transform.position;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/本机位置", __ex); }
            return Vector2.zero;
        }

        // 传送本机玩家（对局内传角色；树屋/大厅传光标）。
        internal static void TeleportLocal(Vector2 world) {
            try {
                List<Character> all = Character.AllCharacters;
                if (all != null) {
                    for (int i = 0; i < all.Count; i++) {
                        Character c = all[i];
                        if (c == null || !c.hasAuthority) continue;
                        Rigidbody2D rb = c.GetComponent<Rigidbody2D>();
                        if (rb != null) rb.position = world;
                        c.transform.position = new Vector3(world.x, world.y, c.transform.position.z);
                        return;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/传送角色", __ex); }
            try {
                LobbyCursor[] cursors = SRApi.FrameObjects<LobbyCursor>();
                if (cursors != null) {
                    for (int i = 0; i < cursors.Length; i++) {
                        LobbyCursor lc = cursors[i];
                        if (lc == null || !lc.hasAuthority) continue;
                        lc.transform.position = new Vector3(world.x, world.y, lc.transform.position.z);
                        return;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/传送光标", __ex); }
        }

        // 重生：传送到最近的自定义重生点；没有自定义点时回到游戏默认出生点。
        internal static void Respawn() {
            try {
                if (_points.Count == 0) {
                    ReadDefault();
                    if (_defaultPoint.HasValue) TeleportLocal(_defaultPoint.Value);
                    return;
                }
                Vector2 me = LocalPosition();
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < _points.Count; i++) {
                    float d = Vector2.Distance(me, _points[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
                TeleportLocal(_points[best]);
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/重生", __ex); }
        }

        // 读游戏自己的默认出生点（Level.GetSpawnPosition）。
        internal static void ReadDefault() {
            if (_defaultPoint.HasValue) return;
            try {
                if (_getSpawnPos == null)
                    _getSpawnPos = SRApi.Ref.Method(typeof(global::Level), "GetSpawnPosition", "重生点/默认出生点");
                global::Level lv = UnityEngine.Object.FindObjectOfType<global::Level>();
                if (lv != null && _getSpawnPos != null) {
                    object r = _getSpawnPos.Invoke(lv, new object[] { 0f });
                    if (r is Vector3) _defaultPoint = (Vector3)r;
                    else if (r is Vector2) _defaultPoint = (Vector2)r;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/读取默认", __ex); }
        }

        // 页面渲染：按钮 + 列表 + 说明。
        internal static void RenderRows() {
            try {
                // 内容标签 + 按钮（右键绑键）+ 快捷键框。
                if (SRApi.Ctl.ActionRow(SRApi.T("重生", "Respawn"),
                        SRApi.T("传送到最近的自定义重生点\n没有自定义点时回游戏默认出生点",
                                "teleport to the nearest custom point\nfalls back to the game default"),
                        SRApi.T("重生（最近点）", "Respawn (nearest)"), _respawnKey, 130f))
                    Respawn();
                if (SRApi.Ctl.ActionRow(SRApi.T("添加重生点", "Add spawn point"),
                        SRApi.T("在当前位置添加一个自定义重生点\n最多 20 个", "add a custom point at your position\nup to 20"),
                        SRApi.T("在当前位置添加", "Add here"), _addKey, 130f)) {
                    Add(LocalPosition());
                    SRApi.Notify(SRApi.T("已添加重生点（", "Spawn point added (") + Count + ")");
                }
                if (SRApi.Ctl.ActionRow(SRApi.T("清空重生点", "Clear spawn points"),
                        SRApi.T("删除全部自定义重生点\n游戏默认出生点不受影响", "remove every custom point\nthe game default is untouched"),
                        SRApi.T("清空", "Clear"), _clearKey, 130f))
                    Clear();
                GUILayout.Space(Sc(4f));
                GUILayout.Label(SRApi.T("已有 ", "Stored: ") + Count + " / " + MaxPoints, SRApi.Ctl.Label);
                if (Count == 0) {
                    GUILayout.Label(SRApi.T("还没有自定义重生点：在地图页右键添加。", "No custom spawn points yet: add them from the map page's right-click menu."), SRApi.Ctl.Label);
                } else {
                    // 逐点一行：序号+坐标 / 传送 / 删除（走核心 ButtonRow，避免手写水平布局）
                    int __del = -1;
                    for (int i = 0; i < Count; i++) {
                        Vector3 pt = At(i);
                        int __hit = SRApi.Ctl.ButtonRow(
                            new string[] {
                                "#" + (i + 1) + "  (" + pt.x.ToString("0.0") + ", " + pt.y.ToString("0.0") + ")",
                                SRApi.T("传送", "Teleport"),
                                SRApi.T("删除", "Delete")
                            },
                            new float[] { 160f, 64f, 64f }, 22f);
                        if (__hit == 0 || __hit == 1) TeleportLocal(pt);
                        else if (__hit == 2) __del = i;
                    }
                    if (__del >= 0) { _points.RemoveAt(__del); SavePoints(); }
                }

            } catch (Exception __ex) { SRApi.Guard.Log("重生点/渲染", __ex); }
        }

        private static float Sc(float v) { return SRApi.Ctl.Sc(v); }
    }
}
