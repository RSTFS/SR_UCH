// 自由模式 · 自身状态 · 穿模子系统（无视对象 / 无视碰撞）。
// 从 FreePlay.Self.cs 拆出来只为控制单文件规模；行为与原实现完全一致。
// 只对本地自己的角色生效：关闭碰撞对 + 临时禁用目标碰撞体 + 每帧清危险标记。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH {
    public partial class SelfState {
        // ================= 无视对象 / 无视碰撞 =================
        private class IgnoreTarget {
            public Collider2D col;
            public bool wasEnabled;
            public bool noDisable;   // 其他角色身上的碰撞体：只关碰撞对，不禁用碰撞体
        }
        private static readonly List<IgnoreTarget> _ignored = new List<IgnoreTarget>();
        private static readonly List<Collider2D> _ignoredMine = new List<Collider2D>();
        private static Collider2D[] _pressCache = new Collider2D[0];
        private static float _ignoreRefreshAt;
        private static Character _ignoreChar;

        // ================= 无视对象 / 无视碰撞 =================
        private static void OnSceneChanged() {
            try { RestoreIgnored(); } catch { /* 静默：换场景时旧碰撞体已销毁，无需恢复 */ }
            try { _ignoreChar = null; _rbCache.Clear(); } catch { /* 静默：缓存清理失败无副作用 */ }
        }

        private static Character LocalCharacter() {
            try {
                Character[] all = UnityEngine.Object.FindObjectsOfType<Character>();
                for (int i = 0; i < all.Length; i++) { if (all[i] != null && all[i].hasAuthority) return all[i]; }
            } catch { /* 静默：场景切换中取不到角色，下次 Tick 再取 */ }
            return null;
        }

        private static void TickIgnore() {
            try {
                bool wantObjects = On(_ignoreObjects);
                bool wantCollision = On(_ignoreCollision);
                if (!wantObjects && !wantCollision) { RestoreIgnored(); return; }
                if (_ignoreChar == null || !_ignoreChar.hasAuthority) _ignoreChar = LocalCharacter();
                Character c = _ignoreChar;
                if (c == null) return;
                if (Time.unscaledTime - _ignoreRefreshAt > 0.5f) {
                    _ignoreRefreshAt = Time.unscaledTime;
                    RestoreIgnored();
                    Rebuild(c, wantObjects, wantCollision);
                }
                PressIgnored();
                ClearHazards(c);
            } catch (Exception __ex) { SRApi.Guard.Log("自身状态/穿模", __ex); }
        }

        private static void PressIgnored() {
            Collider2D[] arr = _pressCache;
            for (int i = 0; i < arr.Length; i++) {
                Collider2D col = arr[i];
                if (col != null && col.enabled) { try { col.enabled = false; } catch { /* 静默：碰撞体销毁中，跳过 */ } }
            }
        }

        private static void ClearHazards(Character c) {
            try {
                ClearHazard(c.FeetPhysicsCollider);
                ClearHazard(c.LowerBodyCollider);
                ClearHazard(c.UpperBodyCollider);
            } catch { /* 静默：角色碰撞体在销毁中，跳过 */ }
        }

        private static void ClearHazard(CheckCollidingPlayer cc) {
            if (cc == null) return;
            try {
                if (cc.Hazard) cc.Hazard = false;
                if (cc.HazardType != null) cc.HazardType = null;
                if (cc.DeathPit) cc.DeathPit = false;
                if (cc.HazardPlacedByPlayer != 0) cc.HazardPlacedByPlayer = 0;
            } catch { /* 静默：属性写入失败不致命，下一帧再清 */ }
        }

        private static void RestoreIgnored() {
            for (int i = 0; i < _ignored.Count; i++) {
                IgnoreTarget t = _ignored[i];
                if (t == null || t.col == null) continue;
                for (int k = 0; k < _ignoredMine.Count; k++) {
                    Collider2D m = _ignoredMine[k];
                    if (m == null) continue;
                    try { Physics2D.IgnoreCollision(m, t.col, false); } catch { /* 静默：恢复碰撞对失败不致命 */ }
                }
                if (t.wasEnabled) { try { t.col.enabled = true; } catch { /* 静默：恢复碰撞体失败不致命 */ } }
            }
            _ignored.Clear();
            _ignoredMine.Clear();
            if (_pressCache.Length != 0) _pressCache = new Collider2D[0];
        }

        private static void Rebuild(Character c, bool wantObjects, bool wantCollision) {
            Collider2D[] mine = c.GetComponentsInChildren<Collider2D>(true);
            HashSet<int> mineIds = new HashSet<int>();
            for (int i = 0; i < mine.Length; i++) if (mine[i] != null) mineIds.Add(mine[i].GetInstanceID());
            HashSet<int> seen = new HashSet<int>();
            if (wantObjects) {
                List<Placeable> all = Placeable.AllPlaceables;
                if (all != null) {
                    // 拆分成多个 DLL 后不能直接调 DestroyBlocks（不同程序集）→ 走它提供的槽位。
                    Func<Placeable, bool> isPlaced = Slots.Require<Func<Placeable, bool>>(SlotIsPlayerPlaced);
                    for (int i = 0; i < all.Count; i++) {
                        Placeable p = all[i];
                        if (p == null) continue;
                        if (isPlaced != null && !isPlaced(p)) continue;   // 关卡自带布局保持实心
                        AddTargets(p.GetComponentsInChildren<Collider2D>(true), mineIds, seen);
                    }
                }
            }
            if (wantCollision) AddTargets(UnityEngine.Object.FindObjectsOfType<Collider2D>(), mineIds, seen);
            _ignoredMine.Clear();
            for (int i = 0; i < mine.Length; i++) {
                if (mine[i] == null) continue;
                _ignoredMine.Add(mine[i]);
                for (int j = 0; j < _ignored.Count; j++) {
                    Collider2D t = _ignored[j].col;
                    if (t == null) continue;
                    try { Physics2D.IgnoreCollision(mine[i], t, true); } catch { /* 静默：单个碰撞对失败不影响其它 */ }
                }
            }
            int pressCount = 0;
            for (int i = 0; i < _ignored.Count; i++) {
                IgnoreTarget t = _ignored[i];
                if (t != null && !t.noDisable && t.col != null) pressCount++;
            }
            if (_pressCache.Length != pressCount) _pressCache = new Collider2D[pressCount];
            int pk = 0;
            for (int i = 0; i < _ignored.Count; i++) {
                IgnoreTarget t = _ignored[i];
                if (t != null && !t.noDisable && t.col != null) _pressCache[pk++] = t.col;
            }
        }

        private static void AddTargets(Collider2D[] cols, HashSet<int> mineIds, HashSet<int> seen) {
            if (cols == null) return;
            for (int i = 0; i < cols.Length; i++) {
                Collider2D col = cols[i];
                if (col == null) continue;
                if (mineIds.Contains(col.GetInstanceID())) continue;   // 自己身上的不收
                if (!seen.Add(col.GetInstanceID())) continue;
                IgnoreTarget t = new IgnoreTarget();
                t.col = col;
                t.wasEnabled = col.enabled;
                // 其他角色身上的碰撞体只关碰撞对，不禁用：禁用会让对方在本地穿地、抽搐。
                Character other = col.GetComponentInParent<Character>();
                t.noDisable = other != null && !other.hasAuthority;
                _ignored.Add(t);
            }
        }

    }
}
