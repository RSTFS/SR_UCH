// 实验 · 关卡边界页：页面渲染 + 边界解除本体 + 解除 UI3 覆盖。

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    // 解除 UI 3 覆盖（已并入「解除关卡边界」）：BlankLevel 里 4 个纯黑 whiteSquare 位于 UI 3 层并盖住界外，
    // 停画它们及该层所有 Renderer。没有自己的开关，完全跟随「解除关卡边界」主开关；
    // 保留 SrFeature 标记仅为功能清单登记与启动自检计数。
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

        // 是否该停画 UI3：跟随「解除关卡边界」主开关（含房主限制，与 Reeval/Tick 一致）。
        internal static bool WantOn { get { return LevelBoundsUnlock.MasterOn; } }

        // 换场景必须清 _wasOn：其为强引用字典，旧 Renderer 已随场景销毁，原值无意义且会逐场累积。
        private static void Reset() { _hitCount = -1; _scanScene = null; _nextScanAt = 0f; _ui3ScanTries = 0; Note = ""; _wasOn.Clear(); }

        // fullSpeed = 边界扫描阶段：每帧都扫（扫到即停，由 Apply 内部的 _hitCount 控制）。
        internal static void Tick(bool fullSpeed = false) {
            try {
                if (_frame == Time.frameCount) return;
                _frame = Time.frameCount;
                // 自己的开关 或 边界主开关（见 WantOn）。
                bool want = WantOn;
                if (!want) { Restore(); return; }
                if (!SRApi.GateMaster) { Restore(); return; }
                Apply(fullSpeed);
            } catch (Exception __ex) { SRApi.Guard.Log("解除UI3 Tick", __ex); }
        }

        private static void Apply(bool fullSpeed = false) {
            try {
                // 扫描时机：首次 / 换场景立即全扫，之后按 unscaled 时间每 2.5 秒复查一次（覆盖加载后才生成的对象）。
                string sc = "";
                try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                float now = Time.unscaledTime;
                // 次数上限：换场景清零，本场景最多扫 Ui3ScanMaxTries 次（≈15 秒）后放弃，避免无限期全场景扫描。
                if (sc != _scanScene) { _scanScene = sc; _ui3ScanTries = 0; }
                // 全速阶段：不按 2.5 秒节流；扫到过（_hitCount>0）就直接停。
                if (_hitCount > 0) return;
                if (_hitCount >= 0 && now < _nextScanAt) return;
                // 全速阶段也只是"尽快"--不是无限全扫：UI3 遮罩要么加载时就在、要么本关没有，
                // 12 次 × 全场景 FindObjectsOfType<Renderer>() 已经是 ~5-10ms 的峰值来源（用户钩子表 7ms 峰）。
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
                // 命中即停止后续全场景复查（新遮罩由场景切换重置后重新扫描）。
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
