// 视觉辅助：淡化 / 隐藏「其他玩家」，方便录屏、截图或看清自己的操作。
//
// 依据（逐项静态核对游戏程序集，均为 public 成员）：
//   Character.sprite / JetPackSR        : SpriteRenderer，public 字段
//   Character.HasJetpack                : public 属性
//   Character.nameTag.maxOpacity        : NameTag 的 public 字段（名牌不透明度上限）
//   Character.associatedLobbyPlayer / associatedGamePlayer : public 字段，均带 isLocalPlayer
//   CharacterOpacityController.character / currentOpacity  : public 字段
//   Util_SpriteRenderer.SetAlpha(SpriteRenderer, float)    : public static 工具方法
//   PlayerManager.instance.playerList / Player.PlayerCharacter : public 成员
//
// 设计要点：
//   1) 纯本地视觉：只改渲染 alpha / currentOpacity，不碰网络、不碰游戏状态，各端各画各的。
//   2) 与 CamHog 二选一：检测到 CamHog 已加载时本功能自动让位（两个 mod 同时写 alpha 会互相打架），
//      页面上给出提示而不是静默失效。
//   3) 只处理「其他玩家」：本地玩家、未入座角色、菜单/树屋阶段一律不动。
//   4) 不缓存 Character 引用（角色会被销毁重建），每帧直接从补丁参数取，零生命周期管理。

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
        private static ConfigEntry<KeyCode> _toggleKey;

        // 缓存常用值：每帧补丁里不走配置对象，改配置时同步刷新。
        private static bool _on;
        private static bool _useDist;
        private static int _dist;
        private static float _alpha;
        private static bool _conflict;   // CamHog 已加载 → 本功能让位
        private static bool _shutdown;   // 核心卸载/重载
        private static bool _fixingOpacity;   // 防止"改成整十"再次触发事件造成递归
        private static bool _uiReg;
        private static readonly System.Collections.Generic.HashSet<int> _faded = new System.Collections.Generic.HashSet<int>();   // 被我们改过透明度的角色

        private static float Alpha { get { return _alpha; } }

        // ============ 注册与配置 ============

        private static void SelfReg() {
            SRApi.LocSec(Sec, "视觉辅助", "Visual Aid");
            SRApi.Nav(Sec, 140);   // 内置栏目 10-130 之后
            SRApi.RegisterPage(Sec, "Main", "画面", "View", 10, RenderPage);

            SRApi.LocKey(Sec, "Enabled", "启用视觉辅助", "Enable visual aid");
            SRApi.LocDesc(Sec, "Enabled",
                "启用视觉辅助\n淡化其他玩家，方便看清自己、录屏或截图\n只影响本机画面，不改变游戏状态",
                "Enable visual aid\nFades other players for a clearer view while recording\nlocal picture only; gameplay is unchanged");
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

            // 开关右侧同一行显示快捷键（与开关逻辑相连：按 F6 就是切换同一个 Enable 开关）。
            SRApi.RowCompanions.Add(Companion);
            SRApi.RowFilters.Add(RowVisible);
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 开关键与开关同行：核心在 Enable 行右侧再画一个「Toggle Key」快捷键控件。
        private static string Companion(ConfigEntryBase e) {
            if (e == null || e.Definition.Section != Sec) return null;
            return e.Definition.Key == "Enabled" ? "Toggle Key" : null;
        }

        // Toggle Key 不再单独成行（它已经在 Enable 行右侧）。
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
                _opacityPercent = plugin.Config.Bind(Sec, "Opacity", 50,
                    new ConfigDescription("其他玩家的不透明度（0 = 完全隐藏，100 = 原样）。", new AcceptableValueRange<int>(0, 100)));
                _useDistance = plugin.Config.Bind(Sec, "Use Distance", true, "只在距离内淡化其他玩家（关闭 = 全部淡化）。");
                _distance = plugin.Config.Bind(Sec, "Distance", 5,
                    new ConfigDescription("淡化距离（1-30）：离本机玩家不超过该距离的其他玩家才淡化。", new AcceptableValueRange<int>(1, 30)));
                _toggleKey = plugin.Config.Bind(Sec, "Toggle Key", KeyCode.F6, "对局内切换视觉辅助的按键。");

                Refresh();
                _enabled.SettingChanged += (s, e) => Refresh();
                _opacityPercent.SettingChanged += (s, e) => { FixOpacityStep(); Refresh(); };
                _useDistance.SettingChanged += (s, e) => Refresh();
                _distance.SettingChanged += (s, e) => Refresh();

                SRApi.RegisterKey("视觉辅助-开关", _toggleKey, "press", delegate {
                    try {
                        if (_toggleKey == null || _enabled == null) return;
                        _enabled.Value = !_enabled.Value;
                        SRApi.Ctl.MarkConfigDirty(_enabled.ConfigFile);
                    } catch (Exception __vk) { SRApi.Guard.Log("视觉辅助/开关键", __vk); }
                });
                // 快捷键与 Enable 开关逻辑相连：按一下就切换同一个配置开关（与页面复选框等价）。

                _conflict = DetectCamHog();
                if (_conflict) SRApi.LogWarn("视觉辅助：检测到 CamHog，已让位（避免两边同时改透明度）。");

                SRApi.OnShutdown(Sec, Shutdown);
                try { Harmony.CreateAndPatchAll(typeof(VisualAid)); }
                catch (Exception e) { SRApi.LogError("视觉辅助 补丁注册失败: " + e.Message); }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/初始化", __ex); }
        }

        private static void Shutdown() { _shutdown = true; RestoreAll(); }
        // 不透明度步进 10：滑条/手输都收敛到整十（0,10,...,100），避免出现 37% 这种值。
        private static void FixOpacityStep() {
            if (_fixingOpacity || _opacityPercent == null) return;
            try {
                int v = Mathf.Clamp(_opacityPercent.Value, 0, 100);
                int r = Mathf.Clamp(Mathf.RoundToInt(v / 10f) * 10, 0, 100);
                if (r != v) { _fixingOpacity = true; _opacityPercent.Value = r; _fixingOpacity = false; }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/整十步进", __ex); }
        }

        private static void Refresh() {
            try {
                _on = _enabled != null && _enabled.Value;
                if (!_on) RestoreAll();   // 关开关：把已经淡化的角色还原，别让游戏一直沿用我们的 currentOpacity
                _useDist = _useDistance == null || _useDistance.Value;
                _dist = _distance != null ? _distance.Value : 5;
                _alpha = Mathf.Clamp01((_opacityPercent != null ? _opacityPercent.Value : 30) / 100f);
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/刷新配置", __ex); }
        }

        // CamHog（同一思路的现成 mod）在加载时会每帧写 alpha，两边同时写会互相覆盖。
        // 检测到它就让位，并在页面上说明，而不是让效果时有时无。
        private static bool DetectCamHog() {
            try {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (asm == null) continue;
                    if (asm.GetName().Name == "CamHog") return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/检测 CamHog", __ex); }
            return false;
        }

        // ============ 每帧应用 ============

        private static bool IsLocal(Character c) {
            if (c.AssociatedLobbyPlayer != null) return c.AssociatedLobbyPlayer.IsLocalPlayer;
            if (c.AssociatedGamePlayer != null) return c.AssociatedGamePlayer.IsLocalPlayer;
            return false;
        }

        private static bool ShouldFade(Character c) {
            if (!_on || _conflict || _shutdown) return false;
            if (c == null || !c.picked) return false;      // 未入座 / 已离场的角色
            if (IsLocal(c)) return false;                  // 自己永远不动
            if (!_useDist || _dist <= 0) return true;      // 不按距离 → 全部淡化
            float nearest = DistanceToNearestLocal(c);
            return nearest >= 0f && nearest <= _dist;
        }

        // 最近本地玩家到该角色的距离；找不到本地玩家（单机树屋等）返回 -1。
        // Character.AllCharacters 是游戏自己的 public 静态列表（含所有角色，不用反射 PlayerManager）。
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
            if (!ShouldFade(c)) { RestoreOne(c); return; }
            _faded.Add(c.GetInstanceID());
            float a = Alpha;
            if (c.SpriteRenderer != null) Util_SpriteRenderer.SetAlpha(c.SpriteRenderer, a);
            if (c.HasJetpack && c.JetPackSR != null) Util_SpriteRenderer.SetAlpha(c.JetPackSR, a);
            if (c.nameTag != null) c.nameTag.maxOpacity = a;
        }

        // 还原一个角色的透明度（只还原我们改过的：调用方先判 _faded）。
        private static void RestoreOne(Character c) {
            if (c == null) return;
            if (!_faded.Remove(c.GetInstanceID())) return;
            try {
                if (c.SpriteRenderer != null) Util_SpriteRenderer.SetAlpha(c.SpriteRenderer, 1f);
                if (c.JetPackSR != null) Util_SpriteRenderer.SetAlpha(c.JetPackSR, 1f);
                if (c.nameTag != null) c.nameTag.maxOpacity = 1f;
                CharacterOpacityController oc = c.GetComponent<CharacterOpacityController>();
                if (oc != null) oc.currentOpacity = 1f;
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/还原", __ex); }
        }

        // 关闭/关机/冲突：遍历所有角色把改过的透明度清掉（角色可能已销毁，安全跳过）。
        private static void RestoreAll() {
            try {
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

        // 游戏的角色透明度控制器每帧会用自己的 currentOpacity 刷新 sprite；
        // 在这里再写一次，保证我们的透明度不会被它盖掉。
        [HarmonyPatch(typeof(CharacterOpacityController), "Update")]
        [HarmonyPostfix]
        private static void AfterOpacityUpdate(CharacterOpacityController __instance) {
            try {
                if (__instance == null) return;
                if (ShouldFade(__instance.character)) __instance.currentOpacity = Alpha;
                else if (__instance.character != null && _faded.Contains(__instance.character.GetInstanceID())) __instance.currentOpacity = 1f;
            } catch (Exception __ex) { SRApi.Guard.Log("视觉辅助/透明度", __ex); }
        }

        // ============ 页面 ============

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
            CfgRow(_useDistance);
            CfgRow(_distance);
        }
    }
}
