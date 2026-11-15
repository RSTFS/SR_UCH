// 视觉辅助：淡化 / 隐藏「其他玩家」，方便录屏、截图或看清自己的操作。
// 注意事项：
// 1) 纯本地视觉：只改渲染 alpha / currentOpacity，不碰网络与游戏状态。
// 2) 与 CamHog 二选一：检测到 CamHog 即让位并在页面提示，避免两边同时写 alpha。
// 3) 只处理其他玩家：本地玩家、未入座角色、菜单/树屋一律不动。
// 4) 不缓存 Character 引用（会被销毁重建），每帧从补丁参数取。

using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.visualaid", "视觉辅助", "1.0")]
    public class VisualAid : ITweak {
        private const string Sec = "视觉辅助";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _opacityPercent;
        private static ConfigEntry<bool> _useDistance;
        private static ConfigEntry<int> _distance;
        private static ConfigEntry<bool> _skipTreehouse;   // 树屋/大厅里不淡化
        private static ConfigEntry<bool> _skipWhenDead;    // 本体死亡后不淡化
        private static ConfigEntry<float> _fadeSeconds;
        // 每个角色的当前显示 alpha（渐变用）；不透明度切换到目标值而不是瞬跳。
        private static readonly System.Collections.Generic.Dictionary<int, float> _curAlpha =
            new System.Collections.Generic.Dictionary<int, float>();
        private static ConfigEntry<KeyCode> _toggleKey;

        // 缓存常用值：每帧补丁里不读配置对象，改配置时刷新。
        private static bool _on;
        private static bool _useDist;
        private static int _dist;
        private static float _alpha;
        private static bool _conflict;   // CamHog 已加载，让位
        private static bool _shutdown;   // 已卸载/重载
        private static bool _fixingOpacity;   // 防止改值递归
        private static bool _uiReg;
        private static readonly System.Collections.Generic.HashSet<int> _faded = new System.Collections.Generic.HashSet<int>();   // 已被改过透明度的角色

        private static float Alpha { get { return _alpha; } }

        // 分区：注册与配置

        private static void SelfReg() {
            SRApi.LocSec(Sec, "视觉辅助", "Visual Aid");
            SRApi.Nav(Sec, 140);   // 侧栏顺序
            SRApi.RegisterPage(Sec, "Main", "画面", "View", 10, RenderPage);

            SRApi.LocKey(Sec, "Enabled", "启用视觉辅助", "Enable visual aid");
            SRApi.LocDesc(Sec, "Enabled",
                "启用视觉辅助\n淡化其他玩家，方便看清自己、录屏或截图\n只影响本机画面，不改变游戏状态",
                "Enable visual aid\nFades other players for a clearer view while recording\nlocal picture only; gameplay is unchanged");
            SRApi.LocKey(Sec, "Skip In Treehouse", "树屋无效", "Disabled in treehouse");
            SRApi.LocDesc(Sec, "Skip In Treehouse",
                "树屋无效\n在树屋/大厅等非对局场景完全不淡化\n避免在树屋里误把角色淡化",
                "disabled in treehouse\nno fading outside a match (treehouse, lobby)\navoids fading characters in the treehouse");
            SRApi.LocKey(Sec, "Skip When Dead", "死后无效", "Disabled while dead");
            SRApi.LocDesc(Sec, "Skip When Dead",
                "死后无效\n本机角色死亡期间停止淡化其他玩家\n复活后自动恢复",
                "disabled while dead\nstops fading while your character is dead\nresumes after respawn");
            SRApi.LocKey(Sec, "Fade Seconds", "淡化时长", "Fade duration");
            SRApi.LocDesc(Sec, "Fade Seconds",
                "淡化/恢复的过渡时长\n0 = 立即切换，数值越大越柔和\n范围 0-2 秒",
                "fade in/out duration\n0 = instant, larger is smoother\nrange 0-2 seconds");
            SRApi.LocKey(Sec, "Opacity Default 30", "不透明度默认值迁移", "Opacity default migration");
            SRApi.LocDesc(Sec, "Opacity Default 30",
                "内部标记：不透明度默认值迁移\n从旧默认 50 改成 30 后置真\n手动改过的不透明度不会被动",
                "internal marker for the opacity default migration\nset once the old default 50 has been moved to 30\nmanually chosen opacity is left untouched");
            SRApi.LocKey(Sec, "Opacity", "其他玩家不透明度", "Other players opacity");
            SRApi.LocDesc(Sec, "Opacity",
                "其他玩家不透明度\n0 = 完全隐藏，100 = 保持原样\n只作用于「其他玩家」，自己永远不变",
                "Other players opacity\n0 = fully hidden, 100 = untouched\nonly affects other players; your character is unchanged");
            SRApi.LocKey(Sec, "Use Distance", "只在近距离淡化", "Fade only when close");
            SRApi.LocDesc(Sec, "Use Distance",
                "只在近距离淡化\n开启后只淡化距离内的其他玩家，远处保持原样\n关闭则淡化全部其他玩家",
                "Fade only when close\nwhen on, only players within the distance are faded\nwhen off, every other player is faded");
            SRApi.LocKey(Sec, "Distance", "淡化距离", "Fade distance");
            SRApi.LocDesc(Sec, "Distance",
                "淡化距离\n离本机玩家不超过该距离（米）的其他玩家才淡化\n1-30；配合上面的开关使用",
                "Fade distance\nother players within this many units are faded\n1-30; used together with the toggle above");
            SRApi.LocKey(Sec, "Toggle Key", "视觉辅助开关", "Visual aid key");
            SRApi.LocDesc(Sec, "Toggle Key",
                "视觉辅助开关\n对局内临时切换视觉辅助\n可改绑，支持组合键",
                "Visual aid key\ntoggles the visual aid in-game\nrebindable, combos supported");

            // 开关右侧同行显示快捷键（按 F6 即切换同一个 Enable 开关）。
            SRApi.RowCompanions.Add(Companion);
            SRApi.RowFilters.Add(RowVisible);
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 开关键与开关同行：核心在 Enable 行右侧再画一个快捷键控件。
        private static string Companion(ConfigEntryBase e) {
            if (e == null || e.Definition.Section != Sec) return null;
            return e.Definition.Key == "Enabled" ? "Toggle Key" : null;
        }

        private static bool RowVisible(ConfigEntryBase e) {
            if (e == null || e.Definition.Section != Sec) return true;
            return e.Definition.Key != "Toggle Key";
        }

        // 数值条目用滑块渲染：范围与 Bind 的 AcceptableValueRange 一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                if (e.Definition.Key == "Opacity") { min = 0f; max = 100f; return true; }
                if (e.Definition.Key == "Distance") { min = 1f; max = 30f; return true; }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Enabled", false, "淡化/隐藏其他玩家，只影响本机画面。");
                _opacityPercent = plugin.Config.Bind(Sec, "Opacity", 30,
                    new ConfigDescription("其他玩家的不透明度（0 = 完全隐藏，100 = 原样）。", new AcceptableValueRange<int>(0, 100)));
                // 旧默认是 50：只迁移仍停在旧默认的配置，用户改过的不动。
                try {
                    ConfigEntry<bool> __mig = plugin.Config.Bind(Sec, "Opacity Default 30", false,
                        "内部标记：不透明度默认值从 50 迁移到 30 是否已执行");
                    if (!__mig.Value && _opacityPercent.Value == 50) {
                        _opacityPercent.Value = 30;
                        __mig.Value = true;
                        SRApi.Ctl.MarkConfigDirty(__mig.ConfigFile);
                    }
                } catch (Exception __om) { SRApi.Guard.Log("视觉辅助/默认值迁移", __om); }
                _fadeSeconds = plugin.Config.Bind(Sec, "Fade Seconds", 0.35f,
                    new ConfigDescription("淡化/恢复的过渡时长（秒）；0 = 立即切换。",
                        new AcceptableValueRange<float>(0f, 2f)));
                _skipTreehouse = plugin.Config.Bind(Sec, "Skip In Treehouse", true,
                    "树屋/大厅无效：在树屋、大厅等非对局场景里完全不淡化其他角色。");
                _skipWhenDead = plugin.Config.Bind(Sec, "Skip When Dead", true,
                    "死后无效：本机角色死亡（含濒死/等待重生）期间停止淡化其他玩家。");
                _useDistance = plugin.Config.Bind(Sec, "Use Distance", true, "只在距离内淡化其他玩家（关闭 = 全部淡化）。");
                _distance = plugin.Config.Bind(Sec, "Distance", 5,
                    new ConfigDescription("淡化距离（1-30）：离本机玩家不超过该距离的其他玩家才淡化。", new AcceptableValueRange<int>(1, 30)));
                _toggleKey = plugin.Config.Bind(Sec, "Toggle Key", KeyCode.F6, "对局内切换视觉辅助的按键。");

                Refresh();
                _enabled.SettingChanged += (s, e) => Refresh();
                _opacityPercent.SettingChanged += (s, e) => { FixOpacityStep(); Refresh(); };
                _useDistance.SettingChanged += (s, e) => Refresh();
                _skipTreehouse.SettingChanged += (s, e) => Refresh();
                _skipWhenDead.SettingChanged += (s, e) => Refresh();
                _distance.SettingChanged += (s, e) => Refresh();

                SRApi.RegisterKey("视觉辅助-开关", _toggleKey, "press", delegate {
                    try {
                        if (_toggleKey == null || _enabled == null) return;
                        _enabled.Value = !_enabled.Value;
                        SRApi.Ctl.MarkConfigDirty(_enabled.ConfigFile);
                    } catch (Exception __vk) { SRApi.Guard.Log("视觉辅助/开关键", __vk); }
                });

                _conflict = DetectCamHog();
                if (_conflict) SRApi.LogWarn("视觉辅助：检测到 CamHog，已让位（避免两边同时改透明度）。");

                SRApi.OnShutdown(Sec, Shutdown);
                try { Harmony.CreateAndPatchAll(typeof(VisualAid)); }
                catch (Exception e) { SRApi.LogError("视觉辅助 补丁注册失败: " + e.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/初始化", __ex); }
        }

        private static void Shutdown() { _shutdown = true; RestoreAll(); }
        // 不透明度步进 10：滑条/手输都收敛到整十，避免出现 37% 这种值。
        private static void FixOpacityStep() {
            if (_fixingOpacity || _opacityPercent == null) return;
            try {
                int v = Mathf.Clamp(_opacityPercent.Value, 0, 100);
                int r = Mathf.Clamp(Mathf.RoundToInt(v / 10f) * 10, 0, 100);
                if (r != v) { _fixingOpacity = true; _opacityPercent.Value = r; _fixingOpacity = false; }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/整十步进", __ex); }
        }

        private static bool _skipTree, _skipDead;

        private static void Refresh() {
            try {
                _shutdown = false;   // 热重载后静态字段保留，须复位否则永久失效
                if (!_conflict && Time.frameCount % 300 == 0) _conflict = DetectCamHog();   // CamHog 可能后加载
                _on = _enabled != null && _enabled.Value;
                if (!_on) RestoreAll();   // 关开关时还原已淡化角色，别让游戏沿用我们的值
                _skipTree = _skipTreehouse != null && _skipTreehouse.Value;
                _skipDead = _skipWhenDead != null && _skipWhenDead.Value;
                _useDist = _useDistance == null || _useDistance.Value;
                _dist = _distance != null ? _distance.Value : 5;
                _alpha = Mathf.Clamp01((_opacityPercent != null ? _opacityPercent.Value : 30) / 100f);
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/刷新配置", __ex); }
        }

        // CamHog 每帧也写 alpha，两边会互相覆盖；检测到即让位并在页面说明，避免效果时有时无。
        private static bool DetectCamHog() {
            try {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (asm == null) continue;
                    if (asm.GetName().Name == "CamHog") return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/检测 CamHog", __ex); }
            return false;
        }

        // 分区：每帧应用

        private static bool IsLocal(Character c) {
            if (c.AssociatedLobbyPlayer != null) return c.AssociatedLobbyPlayer.IsLocalPlayer;
            if (c.AssociatedGamePlayer != null) return c.AssociatedGamePlayer.IsLocalPlayer;
            return false;
        }

        // 本机角色是否已死亡（死亡/濒死/本地判定死亡任一为真）。
        private static bool LocalIsDead() {
            try {
                System.Collections.Generic.List<Character> all = Character.AllCharacters;
                if (all == null) return false;
                for (int i = 0; i < all.Count; i++) {
                    Character c = all[i];
                    if (c == null || !IsLocal(c)) continue;
                    return c.Dead || c.Dying || c.LocallyDead;
                }
            } catch (Exception __ld) { SRApi.Guard.Log("视觉辅助/本机死亡判定", __ld); }
            return false;
        }

        private static bool ShouldFade(Character c) {
            if (!_on || _conflict || _shutdown) return false;
            if (_skipTree && SRApi.Env.InTreehouse) return false;   // 树屋/大厅不淡化
            if (_skipDead && LocalIsDead()) return false;            // 本体死亡期间不淡化
            if (c == null || !c.picked) return false;      // 未入座/已离场
            if (IsLocal(c)) return false;                  // 自己不动
            if (!_useDist || _dist <= 0) return true;      // 不看距离 -> 全部淡化
            float nearest = DistanceToNearestLocal(c);
            return nearest >= 0f && nearest <= _dist;
        }

        // 最近本地玩家到该角色的距离；找不到本地玩家返回 -1。
        // Character.AllCharacters 是游戏的 public 静态列表，无需反射 PlayerManager。
        private static float DistanceToNearestLocal(Character c) {
            try {
                System.Collections.Generic.List<Character> all = Character.AllCharacters;
                if (all == null) return -1f;
                Vector3 pos = c.transform.position;
                float best = -1f;
                for (int i = 0; i < all.Count; i++) {
                    Character lc = all[i];
                    if (lc == null || lc == c || !IsLocal(lc)) continue;
                    float d = Vector3.Distance(pos, lc.transform.position);
                    if (best < 0f || d < best) best = d;
                }
                return best;
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/距离", __ex); return -1f; }
        }

        private static void Apply(Character c) {
            if (c == null) return;
            bool fade = ShouldFade(c);
            float target = fade ? Alpha : 1f;
            int id = c.GetInstanceID();
            float cur;
            _curAlpha.TryGetValue(id, out cur);
            if (_curAlpha.Count == 0) cur = target;      // 首次遇到：直接对齐，避免从 0 淡入
            float dur = _fadeSeconds != null ? Mathf.Clamp(_fadeSeconds.Value, 0f, 2f) : 0.35f;
            if (dur <= 0.001f) cur = target;
            else {
                // 帧率无关的指数逼近：约 0.25S 内走完 95%。
                float k = 1f - Mathf.Exp(-(Time.unscaledDeltaTime * 3f) / Mathf.Max(0.01f, dur));
                cur = Mathf.Lerp(cur, target, Mathf.Clamp01(k));
                if (Mathf.Abs(cur - target) < 0.004f) cur = target;
            }
            if (!fade && Mathf.Abs(cur - 1f) < 0.004f) {
                _curAlpha.Remove(id);
                if (_faded.Remove(id)) RestoreOneImmediate(c);   // 已完全恢复：清标记
                return;
            }
            _curAlpha[id] = cur;
            _faded.Add(id);
            if (c.SpriteRenderer != null) Util_SpriteRenderer.SetAlpha(c.SpriteRenderer, cur);
            if (c.HasJetpack && c.JetPackSR != null) Util_SpriteRenderer.SetAlpha(c.JetPackSR, cur);
            if (c.nameTag != null) c.nameTag.maxOpacity = cur;
        }

        private static void RestoreOne(Character c) {
            if (c == null) return;
            _curAlpha.Remove(c.GetInstanceID());
            if (!_faded.Remove(c.GetInstanceID())) return;
            RestoreOneImmediate(c);
        }

        // 无条件把该角色的透明度写回 1（调用方已决定要还原）。
        private static void RestoreOneImmediate(Character c) {
            if (c == null) return;
            try {
                if (c.SpriteRenderer != null) Util_SpriteRenderer.SetAlpha(c.SpriteRenderer, 1f);
                if (c.JetPackSR != null) Util_SpriteRenderer.SetAlpha(c.JetPackSR, 1f);
                if (c.nameTag != null) c.nameTag.maxOpacity = 1f;
                CharacterOpacityController oc = c.GetComponent<CharacterOpacityController>();
                if (oc != null) oc.currentOpacity = 1f;
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/还原", __ex); }
        }

        // 关闭/关机/冲突：把所有改过的透明度清掉（角色可能已销毁，安全跳过）。
        private static void RestoreAll() {
            try {
                _curAlpha.Clear();
                if (_faded.Count == 0) return;
                System.Collections.Generic.List<Character> all = Character.AllCharacters;
                if (all != null) for (int i = 0; i < all.Count; i++) RestoreOne(all[i]);
                _faded.Clear();
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/全部还原", __ex); }
        }

        [HarmonyPatch(typeof(Character), "Update")]
        [HarmonyPostfix]
        private static void AfterCharacterUpdate(Character __instance) {
            try { Apply(__instance); } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/角色", __ex); }
        }

        // 游戏的透明度控制器每帧用自己的 currentOpacity 刷新 sprite，这里再写一次防被盖掉。
        [HarmonyPatch(typeof(CharacterOpacityController), "Update")]
        [HarmonyPostfix]
        private static void AfterOpacityUpdate(CharacterOpacityController __instance) {
            try {
                if (__instance == null) return;
                if (ShouldFade(__instance.character)) __instance.currentOpacity = Alpha;
                else if (__instance.character != null && _faded.Contains(__instance.character.GetInstanceID())) __instance.currentOpacity = 1f;
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/透明度", __ex); }
        }


        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        private static void RenderPage() {
            SRApi.Ctl.SectionTitle("- " + SRApi.T("其他玩家显示", "Other players") + " -",
                SRApi.T("淡化其他玩家，方便看不清自己 / 录屏 / 截图；只改变本机画面，不影响任何玩法和联机。",
                        "Fades other players for a clearer view; local picture only, no gameplay or network effect."));
            if (_conflict) {
                GUILayout.Label(SRApi.T("检测到 CamHog：本功能已让位（两个 mod 同时改透明度会互相覆盖）。想用本功能请先移除 CamHog。",
                                         "CamHog detected: this feature yields (both mods would overwrite opacity). Remove CamHog to use this."),
                    SRApi.Ctl.LabelWrap);
            }
            CfgRow(_enabled);
            CfgRow(_opacityPercent);
            CfgRow(_skipTreehouse);
            CfgRow(_skipWhenDead);
            CfgRow(_useDistance);
            CfgRow(_distance);
        }
    }
}
