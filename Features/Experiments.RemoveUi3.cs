// 实验 · 关卡边界：解除 UI3 覆盖（停画 UI 3 层 Renderer）。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    // BlankLevel 的 4 个纯黑 whiteSquare 在 UI 3 层盖住界外，停画它们及该层 Renderer。
    // 无独立开关，跟随「解除关卡边界」主开关；SrFeature 仅供功能清单与自检计数。
    [SrFeature("sr.removeui3", "RemoveUI3", "1.0")]
    public class RemoveUI3 : ITweak {
        private static readonly Dictionary<Renderer, bool> _wasOn = new Dictionary<Renderer, bool>();
        internal static string Note = "";
        private static int _frame = -9999;
        private static int _hitCount = -1;
        private static int _ui3ScanTries;                     // 本场景已扫描次数（封顶后放弃）
        private const int Ui3ScanMaxTries = 3;                 // 维持态：5s × 3 ≈ 15 秒
        private const int Ui3FullSpeedMaxTries = 12;           // 扫描阶段：最多 12 次全扫（约 0.2s），不再 240 次连续 FindObjectsOfType
        private static float _nextScanAt;    // 下次全扫时间
        private static string _scanScene;    // 上次扫描的场景名（换场景立即重扫）

        public void Initialize(IFeatureHost plugin) {
            try {
                // 钩子已并入「关卡边界」：不再单独注册 tick（见 LevelBoundsUnlock.Tick）。
                SRApi.RegisterSceneHook(Reset);
                SRApi.RegisterDismissHook(Restore);
            } catch (Exception __ex) { SRApi.Guard.Log("解除UI3 初始化", __ex); }
        }

        internal static bool WantOn { get { return LevelBoundsUnlock.MasterOn; } }

        // 换场景必须清 _wasOn：强引用字典，旧 Renderer 已销毁且会逐场累积。
        private static void Reset() { _hitCount = -1; _scanScene = null; _nextScanAt = 0f; _ui3ScanTries = 0; Note = ""; _wasOn.Clear(); }

        // fullSpeed = 边界扫描阶段：每帧都扫，扫到即停；否则按时间节流。
        internal static void Tick(bool fullSpeed = false) {
            try {
                if (_frame == Time.frameCount) return;
                _frame = Time.frameCount;
                bool want = WantOn;
                if (!want) { Restore(); return; }
                if (!SRApi.GateMaster) { Restore(); return; }
                Apply(fullSpeed);
            } catch (Exception __ex) { SRApi.Guard.Log("解除UI3 Tick", __ex); }
        }

        private static void Apply(bool fullSpeed = false) {
            try {
                // 扫描时机：首次 / 换场景立即全扫，之后按 unscaled 时间定期复查。
                string sc = "";
                try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                float now = Time.unscaledTime;
                // 次数上限：本场景最多扫 Ui3ScanMaxTries 次后放弃，避免无限期全场景扫描。
                if (sc != _scanScene) { _scanScene = sc; _ui3ScanTries = 0; }
                if (_hitCount > 0) return;
                if (_hitCount >= 0 && now < _nextScanAt) return;
                // 全速也只是尽快：全场景 FindObjectsOfType 是主要峰值来源，不做无限全扫。
                if (_ui3ScanTries >= (fullSpeed ? Ui3FullSpeedMaxTries : Ui3ScanMaxTries)) return;
                _ui3ScanTries++;
                _nextScanAt = now + (fullSpeed ? 0.15f : 5f);
                Renderer[] all = SRApi.FrameObjects<Renderer>();   // 同帧共享缓存，避免和别的钩子各扫一遍
                if (all == null) return;
                int n = 0;
                for (int i = 0; i < all.Length; i++) {
                    Renderer r = all[i];
                    if (r == null) continue;
                    string ln;
                    try { ln = r.sortingLayerName; } catch { continue; }
                    if (!IsUI3(ln)) continue;
                    if (!_wasOn.ContainsKey(r)) _wasOn[r] = r.enabled;
                    if (r.enabled) r.enabled = false;
                    n++;
                }
                _hitCount = n;
                if (n > 0) _ui3ScanTries = Ui3FullSpeedMaxTries;
                Note = n > 0 ? ("已解除 UI3 覆盖：停画 " + n + " 个 Renderer")
                             : "未找到 UI3 层上的 Renderer（本关卡可能没有）";
            } catch (Exception __ex) { SRApi.Guard.Log("应用UI3解除", __ex); }
        }

        // UI 3 的显示名可能带空格，sortingLayerName 为配置中的原样字符串。
        private static bool IsUI3(string layerName) {
            if (string.IsNullOrEmpty(layerName)) return false;
            return layerName == "UI 3" || layerName == "UI3";
        }

        private static void Restore() {
            try {
                foreach (KeyValuePair<Renderer, bool> kv in _wasOn) {
                    if (kv.Key == null) continue;
                    try { kv.Key.enabled = kv.Value; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
                _wasOn.Clear();
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            _hitCount = -1;
            Note = "";
        }
    }
}
