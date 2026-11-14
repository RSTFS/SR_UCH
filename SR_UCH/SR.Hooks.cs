// 每帧钩子与性能剖析：Tick/帧尾注册表、关机回调、帧预算降档、钩子耗时统计、UI 强制复位。
// 从 SR.Core.cs 拆出：调度层，与 SR 的生命周期/状态字段无关。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

        // 每帧钩子：功能在 Initialize 里 RegisterTick 注册，SR.Tick 按注册顺序逐个 try/catch 调用。
        // 功能不需被 SR 编译期引用。同一套机制还有 RegisterFrameEnd 钩子（DrawGUI 退出前调用）。
        private sealed class TickHook {
            public string Name;
            public Action Run;
            // 性能剖析（F9 浮层 + 超阈值告警）：滑动平均耗时、上一帧耗时、累计调用与超限次数。
            public float AvgMs;
            public float LastMs;
        // 单次最坏耗时：重钩子（2Hz / 0.5s）的平均值会被空闲帧摊平，只有峰值能反映真实的掉帧风险。
        public float MaxMs;
            public long Calls;
            // 预算驱动的限流：NextDue = 下次允许执行的时间；Interval 由核心按预算压力设置（0 = 每帧）。
            public float NextDue;
            public float Interval;
            // 调用频率（Hz）：0.5s / 2Hz 这类节流钩子按每次耗时判超限必然误报，
            // 只有接近每帧（>=30Hz）的钩子才用单次阈值衡量。
            public float WindowStart;
            public int WindowCalls;
            public float Hz;
            public long Overruns;
            public float WarnedAt = -999f;
        }
        private static readonly List<TickHook> _tickHooks = new List<TickHook>();
        // ---- 帧钩子预算（参考 Unreal Animation Budget Allocator 的固定预算 + 压力平滑 + 防抖）----
        private const float TickBudgetMs = 1.0f;      // 每帧给所有钩子的软预算
        private const float Tier1Factor = 1.5f;       // 压力超过 1.5 倍 -> 非输入钩子降到 30Hz
        private const float Tier2Factor = 2.0f;       // 压力超过 2.0 倍 -> 降到 10Hz
        private const float UpgradeFactor = 0.8f;     // 压力低于 0.8 倍才恢复，避免抖动
        private const int TierSwitchFrames = 20;      // 两次档位切换至少隔多少帧
        private static float _tickPressure = 1f;      // 平滑后的预算压力（1.0 = 刚好用完预算）
        private static int _tickTier;                 // 0 = 全速，1 = 30Hz，2 = 10Hz
        private static int _tierChangedFrame = -1000;
        private static readonly List<TickHook> _frameEndHooks = new List<TickHook>();

        // 按名去重：同名 = 同一钩子的重新注册（覆盖 Run），与 RegisterPage 的同名覆盖语义一致；
        // 匿名委托每次都是新实例，用 Contains 拦不住渲染路径里的重复注册。
        private static void AddOrReplaceHook(List<TickHook> list, string name, Action hook) {
            string n = string.IsNullOrEmpty(name) ? "未命名" : name;
            for (int i = 0; i < list.Count; i++) {
                TickHook h = list[i];
                if (h != null && string.Equals(h.Name, n, StringComparison.Ordinal)) { h.Run = hook; return; }
            }
            list.Add(new TickHook { Name = n, Run = hook });
        }

        // 关机回调登记表（R2）：与 tick 同一套"按名去重、后注册先执行"的语义。
        private static readonly List<TickHook> _shutdownHooks = new List<TickHook>();

        internal static void RegisterShutdown(string name, Action cleanup) {
            if (cleanup == null) return;
            AddOrReplaceHook(_shutdownHooks, name, cleanup);
        }

        // 反向执行关机回调（后注册先跑），每个独立 try；幂等：跑完清空。
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

        // 外部独立 BepInEx 插件形态的功能入口（带 [BepInPlugin] 的 DLL）：插件在自己的 Awake 里调用。
        // MainPlugin 的扫描会跳过这类 DLL 里的 ITweak（BepInEx 已实例化过，再 Initialize 会重复注册）。
        public static void RegisterTweak(ITweak tweak, IFeatureHost host) {
            if (tweak == null) return;
            try {
                tweak.Initialize(host);
            } catch (Exception __ex) {
                Guard.Log("外部插件注册功能 " + tweak.GetType().Name, __ex);
            }
        }

        private static void RunTickHooks() { RunHooks(_tickHooks, "每帧钩子:"); }

        // 每帧收尾（面板关闭的早退分支也要调，否则光标形状会留在屏幕上）
        private static void RunFrameEndHooks() { RunHooks(_frameEndHooks, "每帧收尾:"); }

        // 性能剖析阈值：单 tick 超过 WarnMs 记一次告警（每 tick 每 10 秒最多一条），超过 BadMs 在日志摘要里标 !。
        private const float TickWarnMs = 0.30f;
        private const float TickBadMs = 1.00f;
        private static float _tickProfLastAt = -999f;
        private static float _tickOverrunResetAt;   // 超限计数的 10 秒窗口起点
        private static readonly List<TickHook> _tickProfTop = new List<TickHook>(8);

        // 按键类钩子的命名约定：KEY_* / 含"键" / 含 Hotkey —— 它们只读输入状态，必须每帧执行。
        private static bool IsInputHookName(string name) {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("KEY", StringComparison.Ordinal)) return true;
            if (name.IndexOf("键", StringComparison.Ordinal) >= 0) return true;
            if (name.IndexOf("Hotkey", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 这些钩子里要读"按住/按下"状态，10Hz 会让显示键明显发钝。
            return name == "HUD" || name == "聊天窗口";
        }

        private static void RunHooks(List<TickHook> hooks, string what) {
            float now = Time.realtimeSinceStartup;
            float frameStart = now;
            float spentMs = 0f;
            bool sliced = false;   // 本帧预算已超 -> 后续非输入钩子让路（时间片）
            for (int i = 0; i < hooks.Count; i++) {
                TickHook h = hooks[i];
                if (h == null || h.Run == null) continue;
                bool input = IsInputHookName(h.Name);
                // 按键类永不降频；其余按预算档位限流（Interval 由 UpdateTickBudget 写入）。
                if (!input) {
                    if (h.Interval > 0f && now < h.NextDue) continue;
                    if (sliced) continue;   // 本帧已超预算，剩下的非输入钩子留给下一帧
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
                // 指数滑动平均（0.1 权重 ≈ 最近十帧），避免单帧噪声把排名抖来抖去。
                h.AvgMs = h.Calls <= 1 ? ms : (h.AvgMs * 0.9f + ms * 0.1f);
                if (ms > h.MaxMs) h.MaxMs = ms;
                // 节流钩子（<30Hz）不按单次耗时判超限：0.5s 跑一次的 1ms 只占每秒 0.2%。
                // 节流钩子（0.5s 跑一次）不累计超限：单次耗时与帧预算无可比性，
                // 否则钩子列表里会堆出几千上万条"超限"，掩盖真正每帧变慢的钩子。
                if (h.Hz > 0f && h.Hz < 30f) h.Overruns = 0;
                spentMs += ms;
                // 只对每帧 Tick 钩子做时间片；帧尾钩子（光标形状复位 / 滚轮收口）必须每帧执行。
                if (!input && ReferenceEquals(hooks, _tickHooks) && spentMs > TickBudgetMs * 2f) sliced = true;
                if (ms >= TickWarnMs && h.Hz >= 30f) {
                    h.Overruns++;
                    if (now - h.WarnedAt > 10f) {
                        h.WarnedAt = now;
                        // 用 Warn 而不是 Guard.Log(..., null)：后者会拼上"失败: 未知错误"，把性能告警说成异常。
                        // 明确写"单次峰值"：这类钩子可能只在个别帧重，平均值看不出来。
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

        // 预算压力平滑 + 防抖换档：压力大就降频，压力小且持续才恢复；两次切换至少隔 TierSwitchFrames 帧。
        private static void UpdateTickBudget(float rawPressure) {
            try {
                // 指数平滑（0.1 权重 ≈ 最近十帧），避免单帧尖峰导致档位来回跳。
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
                    // 按键类永远全速
                    h.Interval = IsInputHookName(h.Name) ? 0f : interval;
                }
                // 只在用户开了 Tick Profiling 时写这条：没开剖析就刷日志=噪音
                if (TickProfilingEnabled)
                LogInfo("[性能] 帧钩子预算档位 -> " + (want == 0 ? "全速（每帧）" : want == 1 ? "30Hz" : "10Hz")
                    + "（压力 " + _tickPressure.ToString("0.00") + "，预算 " + TickBudgetMs.ToString("0.0") + "ms/帧）");
            } catch (Exception __ex) { Guard.Log("帧钩子预算", __ex); }
        }

        // 自检页显示当前预算状态
        internal static string TickBudgetText() {
            try {
                return "预算 " + TickBudgetMs.ToString("0.0") + "ms/帧 · 压力 " + _tickPressure.ToString("0.00")
                    + " · 档位 " + (_tickTier == 0 ? "全速" : _tickTier == 1 ? "30Hz" : "10Hz");
            } catch { return ""; }
        }

        // 每 0.5 秒把"最慢的 5 个"写进 F9 浮层 + 一段日志，方便用户直接贴出来。
        private static void PublishTickProfile(float now) {
            try {
                if (!TickProfilingEnabled) return;   // 开关关着不采样，不白算
                if (now - _tickProfLastAt < 0.5f) return;
                _tickProfLastAt = now;
                // 超限计数改成 10 秒窗口：整局累计值会堆到几千，看不出"现在还好不好"。
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
                // 剖析结果只进日志（F9 浮层已删除），一行摘要方便直接贴出来。
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

        // 供自检页「帧钩子」表格使用：每行 = 名称 / 平均 / 峰值 / 调用 / 超限。
        internal static List<string[]> TickProfileRows() {
            List<string[]> rows = new List<string[]>();
            try {
                List<TickHook> all = new List<TickHook>();
                for (int i = 0; i < _tickHooks.Count; i++) if (_tickHooks[i] != null) all.Add(_tickHooks[i]);
                all.Sort(delegate (TickHook a, TickHook b) { return string.Compare(a.Name, b.Name, StringComparison.Ordinal); });
                // 上限 40 行：表格里再多也没人看，页面还要滚。
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

        // 供"健康检查"页导出：把当前 tick 排名拼成多行文本。
        internal static string TickProfileReport() {
            try {
                List<TickHook> all = new List<TickHook>();
                for (int i = 0; i < _tickHooks.Count; i++) if (_tickHooks[i] != null) all.Add(_tickHooks[i]);
                // 按名字排序：按耗时排会让行每 0.5 秒换位置，肉眼没法追踪。
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

        // 强制复位全部 UI 状态（对局开始时调用，避免卡住的管理器 / 地图拖住快照加载握手）。
        public static void ForceResetUiState() {
            try {
                // 对局开始：请各功能收起自己的 UI（功能自己用 RegisterDismissHook 声明收尾方式）。
                try { FireDismissUi(); } catch (Exception __ex) { Guard.Log("对局开始时收起功能 UI", __ex); }
                _visible = false;
                _capturing = null;
                _editText.Clear();
                _editOpen.Clear();
                _enumOptions.Clear();   // 条目对象会重建
                SR.ColorPicker.ClearStates();   // 与 _editText / _editOpen 同批失效：配置重读后条目对象会重建，旧键会留成陈旧项
                ApplyEventSystemGate(); // 重新启用游戏的 EventSystem
                try { ResetWheelState(); } catch (Exception __ex) { Guard.Log("ForceResetUiState", __ex); }
            } catch (Exception __ex) { Guard.Log("对局开始处理", __ex); }
        }

}
}
