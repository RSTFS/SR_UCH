// 自由模式 · 自身状态（完全由 SR 自己实现，不依赖 EX）。
// 六个开关：无敌 / 完美无敌 / 飞天 / 蹲移 / 无视对象 / 无视碰撞。全部只对本地自己生效。
// 以前这套在 EX 里（ExModule.Core/Other），用户要求 SR 的自身状态必须独立 → 整块搬到这里，
// EX 里对应的 UI、配置、补丁、槽位全部删除。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using SR_UCH.Tweaks;
using UnityEngine;

namespace SR_UCH {
    [SrFeature("sr.freeplay.self", "SelfState", "1.0")]
    public class SelfState : ITweak {
        private const string Sec = "自由模式";
        private const string SlotIsPlayerPlaced = "DestroyBlocks.IsPlayerPlaced";

        private static ConfigEntry<bool> _invincible, _godMode, _fly, _crouch, _ignoreObjects, _ignoreCollision;
        private static ConfigEntry<KeyCode> _invincibleKey, _godModeKey, _flyKey, _crouchKey, _ignoreObjectsKey, _ignoreCollisionKey;

        // 统一门控：EX 的"总开关"语义在 SR 侧就是 GateMaster（设置页 All Enabled）。
        private static bool On(ConfigEntry<bool> e) { return e != null && e.Value && SRApi.GateMaster; }

        // ================= 无敌 / 完美无敌 =================
        private static System.Reflection.FieldInfo _invTimerField;
        private static bool _invWasOn;
        private static int _deathBypass;          // 深渊自调用放行深度（用计数，避免嵌套把标志提前清掉）
        private const string CauseFalling = "Falling";

        // ================= 飞天 / 蹲移 =================
        private static System.Reflection.FieldInfo _crouchField;
        private static readonly Dictionary<int, Rigidbody2D> _rbCache = new Dictionary<int, Rigidbody2D>();

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

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _invincible = plugin.Config.Bind(Sec, "Invincible", false, "无敌：免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）。");
                _godMode = plugin.Config.Bind(Sec, "God Mode", false, "完美无敌：在无敌之上再拦强制死亡（深坑、掉出判死线）。");
                _fly = plugin.Config.Bind(Sec, "Fly", false, "飞天：方向键自由飞行（上/下升降，左/右平移，按住 Shift 加速）。");
                _crouch = plugin.Config.Bind(Sec, "Crouch Move", false, "蹲移：蹲下状态下也能左右移动。");
                _ignoreObjects = plugin.Config.Bind(Sec, "Ignore Objects", false, "无视对象：自己穿过玩家放置的道具（关卡自带布局仍实心）。");
                _ignoreCollision = plugin.Config.Bind(Sec, "Ignore Collision", false, "无视碰撞：自己穿过所有实体碰撞（道具 + 场景/地面/墙体）。");
                _invincibleKey = plugin.Config.Bind(Sec, "Invincible Key", KeyCode.None, "无敌的快捷键（EX 页右键按钮可绑）。");
                _godModeKey = plugin.Config.Bind(Sec, "God Mode Key", KeyCode.None, "完美无敌的快捷键。");
                _flyKey = plugin.Config.Bind(Sec, "Fly Key", KeyCode.None, "飞天的快捷键。");
                _crouchKey = plugin.Config.Bind(Sec, "Crouch Move Key", KeyCode.None, "蹲移的快捷键。");
                _ignoreObjectsKey = plugin.Config.Bind(Sec, "Ignore Objects Key", KeyCode.None, "无视对象的快捷键。");
                _ignoreCollisionKey = plugin.Config.Bind(Sec, "Ignore Collision Key", KeyCode.None, "无视碰撞的快捷键。");

                SRApi.RegisterKey("自身状态-无敌", _invincibleKey, "toggle");
                SRApi.RegisterKey("自身状态-完美无敌", _godModeKey, "toggle");
                SRApi.RegisterKey("自身状态-飞天", _flyKey, "toggle");
                SRApi.RegisterKey("自身状态-蹲移", _crouchKey, "toggle");
                SRApi.RegisterKey("自身状态-无视对象", _ignoreObjectsKey, "toggle");
                SRApi.RegisterKey("自身状态-无视碰撞", _ignoreCollisionKey, "toggle");

                SRApi.RegisterTick("自身状态-快捷键", TickKeys);
                SRApi.RegisterTick("自身状态-穿模", TickIgnore);
                SRApi.RegisterSceneHook(OnSceneChanged);
                // 三个补丁类各自独立注册：一个目标不存在只丢那一条，
                // 也不会依赖"CreateAndPatchAll 会不会递归嵌套类型"这种不保险的行为。
                try { Harmony.CreateAndPatchAll(typeof(KillPatch), (string)null); }
                catch (Exception __h1) { SRApi.LogWarn("自身状态/死亡拦截补丁跳过: " + __h1.Message); }
                try { Harmony.CreateAndPatchAll(typeof(SetupDeathPatch), (string)null); }
                catch (Exception __h2) { SRApi.LogWarn("自身状态/广播死亡补丁跳过: " + __h2.Message); }
                try { Harmony.CreateAndPatchAll(typeof(FixedPatch), (string)null); }
                catch (Exception __h3) { SRApi.LogWarn("自身状态/物理帧补丁跳过: " + __h3.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("自身状态/初始化", __ex); }
        }

        private static void SelfReg() {
            SRApi.Ref.Override("Character.KillCharacter", "自身状态：拦截死亡");
            SRApi.Ref.Override("Character.setupDeath", "自身状态：拦截广播死亡");
            SRApi.Ref.Override("Character.invincibleTimer", "自身状态：无敌计时器");
            SRApi.Ref.Override("Character.crouchingDown", "自身状态：蹲移");
            SRApi.LocKey(Sec, "Invincible", "无敌", "Invincible");
            SRApi.LocDesc(Sec, "Invincible", "无敌\n免疫非强制死亡（陷阱/子弹/掉坑/拳击）\n只对本机生效", "Invincible\nimmune to non-forced deaths (traps, bullets, pits, punches)\nlocal only");
            SRApi.LocKey(Sec, "God Mode", "完美无敌", "God mode");
            SRApi.LocDesc(Sec, "God Mode", "完美无敌\n在无敌之上再拦强制死亡（深坑、判死线）\n完全没有死亡反馈，不适合正常玩", "God mode\ninvincible plus forced deaths (pits, death line)\nno death feedback at all; not for normal play");
            SRApi.LocKey(Sec, "Fly", "飞天", "Fly");
            SRApi.LocDesc(Sec, "Fly", "飞天\n方向键自由飞行，Shift 加速\n不按键则悬停在空中", "Fly\nfly freely with the arrow keys; hold Shift to speed up\nstays hovering when no key is held");
            SRApi.LocKey(Sec, "Crouch Move", "蹲移", "Duck move");
            SRApi.LocDesc(Sec, "Crouch Move", "蹲移\n蹲下状态也能左右移动（A/D 或方向键）", "Duck move\nmove left/right while ducking (A/D or arrow keys)");
            SRApi.LocKey(Sec, "Ignore Objects", "无视对象", "Ignore objects");
            SRApi.LocDesc(Sec, "Ignore Objects", "无视对象\n自己穿过玩家放置的道具\n关卡自带布局仍然实心", "Ignore objects\npass through player-placed props\nthe level layout stays solid");
            SRApi.LocKey(Sec, "Ignore Collision", "无视碰撞", "Ignore collision");
            SRApi.LocDesc(Sec, "Ignore Collision", "无视碰撞\n自己穿过所有实体碰撞\n可能掉出地图", "Ignore collision\npass through every solid collider\nmay fall out of the map");
        }

        // ================= 快捷键 =================
        private static void TickKeys() {
            try {
                if (!SRApi.GateMaster) return;
                if (SRApi.Typing) return;
                if (SRApi.ComboKeyDown(_invincibleKey)) Toggle(_invincible, "无敌", "Invincible");
                if (SRApi.ComboKeyDown(_godModeKey)) Toggle(_godMode, "完美无敌", "God mode");
                if (SRApi.ComboKeyDown(_flyKey)) Toggle(_fly, "飞天", "Fly");
                if (SRApi.ComboKeyDown(_crouchKey)) Toggle(_crouch, "蹲移", "Duck move");
                if (SRApi.ComboKeyDown(_ignoreObjectsKey)) Toggle(_ignoreObjects, "无视对象", "Ignore objects");
                if (SRApi.ComboKeyDown(_ignoreCollisionKey)) Toggle(_ignoreCollision, "无视碰撞", "Ignore collision");
            } catch (Exception __ex) { SRApi.Guard.Log("自身状态/快捷键", __ex); }
        }

        private static void Toggle(ConfigEntry<bool> e, string zh, string en) {
            if (e == null) return;
            e.Value = !e.Value;
            SRApi.Notify(SRApi.T(zh, en) + (e.Value ? SRApi.T("：开", ": on") : SRApi.T("：关", ": off")));
        }

        // ================= 死亡拦截 =================
        // 两条死亡路径：本地 KillCharacter 与房主广播的 setupDeath，两处都要拦，否则只挡一半。
        [HarmonyPatch(typeof(Character), "KillCharacter")]
        internal static class KillPatch {
            [HarmonyPrefix]
            private static bool Prefix(Character __instance, string cause, bool deathFreezeOn, int causedByPlayerNumber, bool force) {
                try {
                    if (__instance == null || !__instance.hasAuthority) return true;
                    if (On(_godMode)) return false;                       // 完美无敌：连强制死亡一起拦
                    if (force) return true;                               // 原版语义：强制死亡生效
                    if (_deathBypass > 0) return true;
                    bool ignoreMode = On(_ignoreObjects) || On(_ignoreCollision);
                    if (!On(_invincible) && !ignoreMode) return true;
                    if (cause == CauseFalling) {
                        // 深渊：普通无敌不拦。用计数放行一次 force 死亡，避免无限递归。
                        _deathBypass++;
                        try { __instance.KillCharacter(cause, deathFreezeOn, causedByPlayerNumber, true); }
                        finally { if (_deathBypass > 0) _deathBypass--; }
                        return false;
                    }
                    return false;                                          // 普通死亡：拦下
                } catch { return true; /* 静默：拦截失败就放行，宁可多死一次也不卡死死亡流程 */ }
            }
        }

        [HarmonyPatch(typeof(Character), "setupDeath")]
        internal static class SetupDeathPatch {
            [HarmonyPrefix]
            private static bool Prefix(Character __instance, string cause) {
                try {
                    if (__instance == null || !__instance.hasAuthority) return true;
                    if (_deathBypass > 0) return true;
                    if (On(_godMode)) return false;
                    bool ignoreMode = On(_ignoreObjects) || On(_ignoreCollision);
                    if (!On(_invincible) && !ignoreMode) return true;
                    if (cause == CauseFalling) return true;                 // 深渊允许死亡（与上面一致）
                    return false;
                } catch { return true; /* 静默：同上，拦截失败即放行 */ }
            }
        }

        // 飞天 + 蹲移 + 无敌续期合并成一个 FixedUpdate 后缀，减少每物理帧的 Harmony 调用。
        [HarmonyPatch(typeof(Character), "FixedUpdate")]
        internal static class FixedPatch {
            [HarmonyPostfix]
            private static void Postfix(Character __instance) {
                try {
                    ApplyFly(__instance);
                    KeepCrouch(__instance);
                    KeepInvincible(__instance);
                } catch (Exception __ex) { SRApi.Guard.Log("自身状态/物理帧", __ex); }
            }
        }

        private static Rigidbody2D Rb(Character c) {
            try {
                if (c == null) return null;
                int id = c.GetInstanceID();
                Rigidbody2D rb;
                if (_rbCache.TryGetValue(id, out rb) && rb != null) return rb;
                rb = c.GetComponent<Rigidbody2D>();
                if (_rbCache.Count > 64) _rbCache.Clear();
                _rbCache[id] = rb;
                return rb;
            } catch { return null; /* 静默：取不到刚体就当没有，下一物理帧再取 */ }
        }

        private static void ApplyFly(Character c) {
            try {
                if (!On(_fly)) return;
                if (c == null || !c.hasAuthority || c.Success) return;
                Rigidbody2D rb = Rb(c);
                if (rb == null) return;
                float mul = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 2f : 1f;
                float vx = 0f, vy = 0f;
                if (Input.GetKey(KeyCode.UpArrow)) vy = 9f * mul;
                else if (Input.GetKey(KeyCode.DownArrow)) vy = -9f * mul;
                if (Input.GetKey(KeyCode.LeftArrow)) vx = -7f * mul;
                else if (Input.GetKey(KeyCode.RightArrow)) vx = 7f * mul;
                rb.velocity = new Vector2(vx, vy);
            } catch { /* 静默：物理帧高频路径，失败下帧重试，不写日志 */ }
        }

        private static void KeepCrouch(Character c) {
            try {
                if (!On(_crouch) || On(_fly)) return;   // 飞天已完全接管速度
                if (c == null || !c.hasAuthority) return;
                if (_crouchField == null) _crouchField = SRApi.Ref.Field(typeof(Character), "crouchingDown", "自身状态：蹲移");
                if (_crouchField == null) return;
                if (!(bool)_crouchField.GetValue(c)) return;
                Rigidbody2D rb = Rb(c);
                if (rb == null) return;
                float h = 0f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h = -1f;
                else if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h = 1f;
                rb.velocity = new Vector2(h * c.RunSpeed * 0.9f, rb.velocity.y);
            } catch { /* 静默：物理帧高频路径，失败下帧重试，不写日志 */ }
        }

        // 无敌/完美无敌共用游戏自己的重生无敌计时器：开着就一直续期，关掉时只清我们拉高的那一份。
        private static void KeepInvincible(Character c) {
            try {
                if (c == null || !c.hasAuthority) return;
                if (_invTimerField == null) _invTimerField = SRApi.Ref.Field(typeof(Character), "invincibleTimer", "自身状态：无敌计时器");
                bool want = On(_godMode) || On(_invincible);
                if (want) {
                    _invWasOn = true;
                    float t = 0f;
                    if (_invTimerField != null) { try { t = (float)_invTimerField.GetValue(c); } catch { t = 0f; } }
                    if (t < 500f) c.StartInvincibleTimer(999f);
                } else if (_invWasOn) {
                    _invWasOn = false;
                    if (_invTimerField != null) {
                        try { if ((float)_invTimerField.GetValue(c) >= 500f) _invTimerField.SetValue(c, 0f); } catch { /* 静默：字段写入失败不影响死亡流程 */ }
                    }
                }
            } catch { /* 静默：物理帧高频路径，读不到计时器就下帧再续 */ }
        }

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

        // ================= 页面（自由模式页的「自身状态」分区） =================
        internal static void RenderRows() {
            try {
                float w = SRApi.Ctl.Sc(150f);
                float gap = SRApi.Ctl.Sc(8f);
                GUILayout.BeginHorizontal();
                try {
                    if (SRApi.Ctl.SelfToggleButton(SRApi.T("无敌：", "Invincible: "), SRApi.T("自己免疫非强制死亡", "Immune to non-forced deaths"), On(_invincible), _invincibleKey, w, true)) Toggle(_invincible, "无敌", "Invincible");
                    GUILayout.Space(gap);
                    if (SRApi.Ctl.SelfToggleButton(SRApi.T("飞天：", "Fly: "), SRApi.T("方向键自由飞行", "Fly with the arrow keys"), On(_fly), _flyKey, w, true)) Toggle(_fly, "飞天", "Fly");
                    GUILayout.Space(gap);
                    if (SRApi.Ctl.SelfToggleButton(SRApi.T("蹲移：", "Duck: "), SRApi.T("保持蹲下自由移动", "Stay ducked and move freely"), _crouch != null && _crouch.Value, _crouchKey, w, true)) Toggle(_crouch, "蹲移", "Duck move");
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
                GUILayout.Space(SRApi.Ctl.Sc(4f));
                GUILayout.BeginHorizontal();
                try {
                    if (SRApi.Ctl.SelfToggleButton(SRApi.T("完美无敌：", "God mode: "), SRApi.T("连强制死亡、深坑、掉出判死线都拦", "Also blocks forced deaths (pits, death line)"), On(_godMode), _godModeKey, w, true)) Toggle(_godMode, "完美无敌", "God mode");
                    GUILayout.Space(gap);
                    if (SRApi.Ctl.SelfToggleButton(SRApi.T("无视对象：", "Ignore objects: "), SRApi.T("自己穿过玩家放置的道具", "Pass through player-placed props"), On(_ignoreObjects), _ignoreObjectsKey, w, true)) Toggle(_ignoreObjects, "无视对象", "Ignore objects");
                    GUILayout.Space(gap);
                    if (SRApi.Ctl.SelfToggleButton(SRApi.T("无视碰撞：", "Ignore collision: "), SRApi.T("自己穿过所有实体碰撞", "Pass through every solid collider"), On(_ignoreCollision), _ignoreCollisionKey, w, true)) Toggle(_ignoreCollision, "无视碰撞", "Ignore collision");
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
            } catch (Exception __ex) { SRApi.Guard.Log("自身状态/渲染", __ex); }
        }
    }
}
