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
        private static readonly List<Vector3> _points = new List<Vector3>();
        private static Vector3? _defaultPoint;
        private static MethodInfo _getSpawnPos;

        internal static int Count { get { return _points.Count; } }
        internal static IList<Vector3> All { get { return _points; } }
        internal static Vector3 At(int i) { return (i >= 0 && i < _points.Count) ? _points[i] : Vector3.zero; }

        internal static void Initialize(IFeatureHost plugin) {
            try {
                _enabled = plugin.Config.Bind(Sec, "Spawn Points", false,
                    "自定义重生点：在地图页右键添加/传送/删除，仅自由模式可用。");
                SRApi.LocKey(Sec, "Spawn Points", "自定义重生点", "Custom spawn points");
                SRApi.LocDesc(Sec, "Spawn Points",
                    "自定义重生点\n地图页右键可添加、传送、删除重生点\n最多 20 个；仅自由模式可用",
                    "custom spawn points\nright-click on the map to add, teleport or remove\nup to 20; freeplay only");
                SRApi.RegisterSceneHook(delegate { _points.Clear(); _defaultPoint = null; });
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/初始化", __ex); }
        }

        internal static void Add(Vector2 world) {
            try {
                if (_enabled != null && !_enabled.Value) return;
                if (_points.Count >= MaxPoints) _points.RemoveAt(0);
                _points.Add(new Vector3(world.x, world.y, 0f));
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/添加", __ex); }
        }

        internal static void Clear() {
            try { _points.Clear(); } catch (Exception __ex) { SRApi.Guard.Log("重生点/清空", __ex); }
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
                if (best >= 0) _points.RemoveAt(best);
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
                int hit = SRApi.Ctl.ButtonRow(
                    new string[] {
                        SRApi.T("重生（最近点）", "Respawn (nearest)"),
                        SRApi.T("在当前位置添加", "Add at current position"),
                        SRApi.T("删除最近", "Remove nearest"),
                        SRApi.T("清空", "Clear")
                    },
                    new float[] { 140f, 150f, 100f, 70f }, 24f);
                if (hit == 0) Respawn();
                else if (hit == 1) { Add(LocalPosition()); SRApi.Notify(SRApi.T("已添加重生点（", "Spawn point added (") + Count + ")"); }
                else if (hit == 2) RemoveNearest();
                else if (hit == 3) Clear();
                GUILayout.Space(Sc(4f));
                SRApi.Ctl.LabeledRow(SRApi.T("自定义重生点", "Custom spawn points"),
                    SRApi.T("已有数量 / 上限", "how many are stored"),
                    null, 0f);
                GUILayout.Label(Count + " / " + MaxPoints, SRApi.Ctl.Label);
                if (Count == 0) {
                    GUILayout.Label(SRApi.T("还没有自定义重生点：在地图页右键添加。", "No custom spawn points yet: add them from the map page's right-click menu."), SRApi.Ctl.Label);
                }
                GUILayout.Space(Sc(4f));
                GUILayout.Label(SRApi.T("提示：地图页右键菜单可传送、添加、删除重生点；仅自由模式可用。",
                    "Tip: use the map page's right-click menu to teleport, add or remove spawn points; freeplay only."), SRApi.Ctl.Label);
            } catch (Exception __ex) { SRApi.Guard.Log("重生点/渲染", __ex); }
        }

        private static float Sc(float v) { return SRApi.Ctl.Sc(v); }
    }
}
