// 每帧钩子与性能剖析：Tick/帧尾注册表、关机回调、帧预算降档、耗时统计、UI 强制复位。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

        // 每帧钩子：功能在 Initialize 里 RegisterTick 注册，按注册顺序逐个 try/catch 调用。
        private sealed class TickHook {
            public string Name;
            public Action Run;
            // 性能剖析：滑动平均耗时、上一帧耗时、累计调用与超限次数。
            public float AvgMs;
            public float LastMs;
        public float MaxMs;
            public long Calls;
            // 预算限流：NextDue = 下次允许执行时间，Interval 由核心按预算压力设置（0 = 每帧）。
            public float NextDue;
            public float Interval;
            // 调用频率（Hz）：只有接近每帧（>=30Hz）的钩子才用单次耗时阈值。
            public float WindowStart;
            public int WindowCalls;
            public float Hz;
            public long Overruns;
            public float WarnedAt = -999f;
        }
        private static readonly List<TickHook> _tickHooks = new List<TickHook>();
        // 分区：帧钩子预算（固定预算 + 压力平滑 + 防抖换档）
        private const float TickBudgetMs = 1.0f;      // 每帧软预算
        private const float Tier1Factor = 1.5f;       // 压力 >1.5 倍 -> 30Hz
        private const float Tier2Factor = 2.0f;       // 压力 >2.0 倍 -> 10Hz
        private const float UpgradeFactor = 0.8f;     //  <0.8 倍才恢复，避免抖动
        private const int TierSwitchFrames = 20;      // 档位切换最小间隔帧
        private static float _tickPressure = 1f;      // 平滑压力（1.0 = 用满预算）
        private static int _tickTier;                 // 0=全速 1=30Hz 2=10Hz
        private static int _tierChangedFrame = -1000;
        private static readonly List<TickHook> _frameEndHooks = new List<TickHook>();

        // 按名去重：同名视为同一钩子的重新注册（覆盖 Run）；匿名委托是新实例，重复注册拦不住。
        private static void AddOrReplaceHook(List<TickHook> list, string name, Action hook) {
            string n = string.IsNullOrEmpty(name) ? "未命名" : name;
            for (int i = 0; i < list.Count; i++) {
                TickHook h = list[i];
                if (h != null && string.Equals(h.Name, n, StringComparison.Ordinal)) { h.Run = hook; return; }
            }
            list.Add(new TickHook { Name = n, Run = hook });
        }

        private static readonly List<TickHook> _shutdownHooks = new List<TickHook>();

        internal static void RegisterShutdown(string name, Action cleanup) {
            if (cleanup == null) return;
            AddOrReplaceHook(_shutdownHooks, name, cleanup);
        }

        // 反向执行关机回调（后注册先跑），逐个 try；幂等：跑完清空。
        internal static void RunShutdowns() {
            try {
                for (int i = _shutdownHooks.Count - 1; i >= 0; i--) {
                    TickHook h = _shutdownHooks[i];
                    if (h == null || h.Run == null) continue;
                    try { h.Run(); } catch (Exception __ex) { Guard.Log("关机回调 " + h.Name, __ex); }
                }
                if (_shutdownHooks.Count > 0) LogInfo("[SR] 已执行 " + _shutdownHooks.Count + " 个关机回调");
                _shutdownHooks.Clear();
            } catch (Exception __ex) { Guard.Log("关机回调批处理", __ex); }
        }

        public static void RegisterTick(string name, Action hook) {
            if (hook == null) return;
            AddOrReplaceHook(_tickHooks, name, hook);
        }

        public static void RegisterFrameEnd(string name, Action hook) {
            if (hook == null) return;
            AddOrReplaceHook(_frameEndHooks, name, hook);
        }

        // 外部独立插件（带 [BepInPlugin] 的 DLL）在自己的 Awake 里调用；MainPlugin 扫描会跳过这类 DLL。
        public static void RegisterTweak(ITweak tweak, IFeatureHost host) {
            if (tweak == null) return;
            try {
                tweak.Initialize(host);
            } catch (Exception __ex) {
                Guard.Log("外部插件注册功能 " + tweak.GetType().Name, __ex);
            }
        }

        private static void RunTickHooks() { RunHooks(_tickHooks, "每帧钩子:"); }

        // 每帧收尾：面板关闭的早退分支也必须调，否则光标形状会留在屏幕上。
        private static void RunFrameEndHooks() { RunHooks(_frameEndHooks, "每帧收尾:"); }

        // 剖析阈值：单 tick 超 WarnMs 告警（每钩子 10 秒最多一条），超 BadMs 在摘要里标 !。
        private const float TickWarnMs = 0.30f;
        private const float TickBadMs = 1.00f;
        private static float _tickProfLastAt = -999f;
        private static float _tickOverrunResetAt;   // 超限 10 秒窗口起点
        private static readonly List<TickHook> _tickProfTop = new List<TickHook>(8);

        // 按键类钩子命名约定：KEY_* / 含“键” / 含 Hotkey，它们只读输入状态，必须每帧执行。
        private static bool IsInputHookName(string name) {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("KEY", StringComparison.Ordinal)) return true;
            if (name.IndexOf("键", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("Hotkey", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return name == "HUD" || name == "聊天窗口";
        }

        private static void RunHooks(List<TickHook> hooks, string what) {
            float now = Time.realtimeSinceStartup;
            float frameStart = now;
            float spentMs = 0f;
            bool sliced = false;   // 本帧已超预算，非输入钩子让路
            for (int i = 0; i < hooks.Count; i++) {
                TickHook h = hooks[i];
                if (h == null || h.Run == null) continue;
                bool input = IsInputHookName(h.Name);
                // 按键类永不降频；其余按 Interval 限流（由 UpdateTickBudget 写入）。
                if (!input) {
                    if (h.Interval > 0f && now < h.NextDue) continue;
                    if (sliced) continue;   // 本帧已超预算，剩下的留给下一帧
                    h.NextDue = now + h.Interval;
                }
                float t0 = Time.realtimeSinceStartup;
                try { h.Run(); }
                catch (Exception __ex) { Guard.Log(what + h.Name, __ex); }
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                h.LastMs = ms;
                h.Calls++;
                if (now - h.WindowStart >= 1f) {
                    if (h.WindowStart > 0f) h.Hz = h.WindowCalls / Mathf.Max(0.0001f, now - h.WindowStart);
                    h.WindowStart = now; h.WindowCalls = 0;
                }
                h.WindowCalls++;
                // 指数滑动平均（0.1 权重 ≈ 最近十帧），避免单帧噪声抖动排名。
                h.AvgMs = h.Calls <= 1 ? ms : (h.AvgMs * 0.9f + ms * 0.1f);
                if (ms > h.MaxMs) h.MaxMs = ms;
                // 节流钩子（<30Hz）不按单次耗时判超限，也不累计超限，否则会淹没真正变慢的钩子。
                if (h.Hz > 0f && h.Hz < 30f) h.Overruns = 0;
                spentMs += ms;
                // 只对 Tick 钩子做时间片；帧尾钩子必须每帧执行。
                if (!input && ReferenceEquals(hooks, _tickHooks) && spentMs > TickBudgetMs * 2f) sliced = true;
                if (ms >= TickWarnMs && h.Hz >= 30f) {
                    h.Overruns++;
                    if (now - h.WarnedAt > 10f) {
                        h.WarnedAt = now;
                        // 用 Warn 而非 Guard.Log(..., null)：后者会拼出“失败: 未知错误”，把性能告警说成异常。
                        if (TickProfilingEnabled)
                            LogWarn("[性能] 帧钩子单次峰值 " + h.Name + " " + ms.ToString("0.00") + "ms"
                            + "（峰值 " + h.MaxMs.ToString("0.00") + "ms / 平均 " + h.AvgMs.ToString("0.00") + "ms / 阈值 "
                            + TickWarnMs.ToString("0.00") + "ms，累计 " + h.Overruns + " 次超限）");
                    }
                }
            }
            if (ReferenceEquals(hooks, _tickHooks)) UpdateTickBudget(spentMs / TickBudgetMs);
            PublishTickProfile(now);
        }

        // 压力平滑 + 防抖换档：压力大降频、持续小才恢复；两次切换至少隔 TierSwitchFrames 帧。
        private static void UpdateTickBudget(float rawPressure) {
            try {
                _tickPressure = _tickPressure * 0.9f + rawPressure * 0.1f;
                int want = _tickTier;
                if (_tickPressure > Tier2Factor) want = 2;
                else if (_tickPressure > Tier1Factor) want = 1;
                else if (_tickPressure < UpgradeFactor) want = 0;
                if (want == _tickTier) return;
                if (Time.frameCount - _tierChangedFrame < TierSwitchFrames) return;
                _tickTier = want;
                _tierChangedFrame = Time.frameCount;
                float interval = want == 2 ? 0.1f : (want == 1 ? 1f / 30f : 0f);
                for (int i = 0; i < _tickHooks.Count; i++) {
                    TickHook h = _tickHooks[i];
                    if (h == null) continue;
                    h.Interval = IsInputHookName(h.Name) ? 0f : interval;
                }
                // 仅开着 Tick Profiling 时写日志，否则是噪音。
                if (TickProfilingEnabled)
                LogInfo("[性能] 帧钩子预算档位 -> " + (want == 0 ? "全速（每帧）" : want == 1 ? "30Hz" : "10Hz")
                    + "（压力 " + _tickPressure.ToString("0.00") + "，预算 " + TickBudgetMs.ToString("0.0") + "ms/帧）");
            } catch (Exception __ex) { Guard.Log("帧钩子预算", __ex); }
        }

        internal static string TickBudgetText() {
            try {
                return "预算 " + TickBudgetMs.ToString("0.0") + "ms/帧 · 压力 " + _tickPressure.ToString("0.00")
                    + " · 档位 " + (_tickTier == 0 ? "全速" : _tickTier == 1 ? "30Hz" : "10Hz");
            } catch { return ""; }
        }

        // 每 0.5 秒把最慢的 5 个钩子写一段日志，便于直接贴出。
        private static void PublishTickProfile(float now) {
            try {
                if (!TickProfilingEnabled) return;   // 开关关着不采样
                if (now - _tickProfLastAt < 0.5f) return;
                _tickProfLastAt = now;
                // 超限计数走 10 秒窗口：整局累计会堆到几千，看不出当前状态。
                if (now - _tickOverrunResetAt >= 10f) {
                    _tickOverrunResetAt = now;
                    for (int i = 0; i < _tickHooks.Count; i++) { if (_tickHooks[i] != null) _tickHooks[i].Overruns = 0; }
                }
                _tickProfTop.Clear();
                for (int i = 0; i < _tickHooks.Count; i++) {
                    TickHook h = _tickHooks[i];
                    if (h == null || h.AvgMs <= 0f) continue;
                    _tickProfTop.Add(h);
                }
                if (_tickProfTop.Count == 0) return;
                _tickProfTop.Sort(delegate (TickHook a, TickHook b) { return b.AvgMs.CompareTo(a.AvgMs); });
                int n = Mathf.Min(5, _tickProfTop.Count);
                float total = 0f;
                for (int i = 0; i < _tickProfTop.Count; i++) total += _tickProfTop[i].AvgMs;
                StringBuilder sb = new StringBuilder();
                sb.Append("[性能] 帧钩子合计 ").Append(total.ToString("0.00")).Append("ms（")
                  .Append(_tickProfTop.Count).Append(" 个）预算档位 ").Append(_tickTier);
                for (int i = 0; i < n; i++) {
                    TickHook h = _tickProfTop[i];
                    string mark = h.AvgMs >= TickBadMs ? " !!" : (h.AvgMs >= TickWarnMs ? " !" : "");
                    sb.Append(" | ").Append(h.Name).Append(' ').Append(h.AvgMs.ToString("0.00")).Append("ms")
                      .Append(mark)
                      .Append(h.Overruns > 0 ? "×" + h.Overruns : "");
                }
                LogInfo(sb.ToString());
            } catch (Exception __ex) { Guard.Log("帧钩子性能剖析", __ex); }
        }

        // 自检页「帧钩子」表格行：名称 / 平均 / 峰值 / 调用 / 超限。
        internal static List<string[]> TickProfileRows() {
            List<string[]> rows = new List<string[]>();
            try {
                List<TickHook> all = new List<TickHook>();
                for (int i = 0; i < _tickHooks.Count; i++) if (_tickHooks[i] != null) all.Add(_tickHooks[i]);
                all.Sort(delegate (TickHook a, TickHook b) { return string.Compare(a.Name, b.Name, StringComparison.Ordinal); });
                int n = Mathf.Min(40, all.Count);
                for (int i = 0; i < n; i++) {
                    TickHook h = all[i];
                    rows.Add(new string[] {
                        h.Name ?? "",
                        h.AvgMs.ToString("0.000") + "ms",
                        h.MaxMs.ToString("0.000") + "ms",
                        (h.Hz > 0f ? h.Hz.ToString("0.0") + "/s" : "-") + (h.Interval > 0f ? "↓" : ""),
                        h.Overruns > 0 ? h.Overruns.ToString() : ""
                    });
                }
            } catch (Exception __ex) { Guard.Log("帧钩子行数据", __ex); }
            return rows;
        }

        // 健康检查页导出：当前 tick 排名拼成多行文本。
        internal static string TickProfileReport() {
            try {
                List<TickHook> all = new List<TickHook>();
                for (int i = 0; i < _tickHooks.Count; i++) if (_tickHooks[i] != null) all.Add(_tickHooks[i]);
                // 按名字排序：按耗时排会让行每 0.5 秒换位，肉眼无法追踪。
                all.Sort(delegate (TickHook a, TickHook b) { return string.Compare(a.Name, b.Name, System.StringComparison.Ordinal); });
                StringBuilder sb = new StringBuilder();
                float total = 0f;
                for (int i = 0; i < all.Count; i++) total += all[i].AvgMs;
                sb.Append("帧钩子 ").Append(all.Count).Append(" 个，合计 ").Append(total.ToString("0.00")).AppendLine("ms");
                int n = Mathf.Min(15, all.Count);
                for (int i = 0; i < n; i++) {
                    TickHook h = all[i];
                    if (h.AvgMs <= 0.001f) break;
                    sb.Append("  ").Append(h.AvgMs.ToString("0.000")).Append("ms  ").Append(h.Name)
                      .Append("  调用=").Append(h.Calls).Append("  超限=").Append(h.Overruns).AppendLine();
                }
                return sb.ToString();
            } catch (Exception __ex) { Guard.Log("帧钩子报告", __ex); return ""; }
        }

        // 强制复位全部 UI 状态（对局开始时调用，避免卡住的管理器拖住快照加载握手）。
        public static void ForceResetUiState() {
            try {
                // 各功能收起自己的 UI（由 RegisterDismissHook 声明收尾方式）。
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("对局开始时收起功能 UI", __ex); }
                _visible = false;
                _capturing = null;
                _editText.Clear();
                _editOpen.Clear();
                _enumOptions.Clear();   // 条目对象会重建。
                SR.ColorPicker.ClearStates();   // 与 _editText/_editOpen 同批失效，否则旧键留成陈旧项
                ApplyEventSystemGate(); // 重新启用 EventSystem
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("ForceResetUiState", __ex); }
            } catch (Exception __ex) { Guard.Log("对局开始处理", __ex); }
        }

}
}
