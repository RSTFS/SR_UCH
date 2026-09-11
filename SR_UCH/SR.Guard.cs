using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Guard（可观测性：统一带日志的 try/catch 包装）====
//
// 提示词第 7 节“可观测性”：项目里有大量 `catch { }` 空吞异常，出问题时完全无线索。
// 这里提供统一包装（ExRef / DestroyBlocks.BroadcastPieceDestroyed 原本已有类似模式，只是未推广）。
//
// 关键设计：**同一条 what 在 5 秒内只记一次**。这样即使是每帧路径上的 catch
//（相机、方块高亮等）也可以安全接入，不会因为持续失败而刷屏日志。
// 反射探测型 catch（ExRef 找可选成员、属性/方法不存在是常态）保持不接入——那是预期分支，不是错误。
        public static class Guard {
            // 执行一个动作；异常时记 Warning（带 what 标识），不抛出。
            public static void Try(string what, Action action) {
                try { action(); }
                catch (Exception ex) { Warn(what, ex); }
            }

            // 执行一个取值动作；异常时记 Warning 并返回 fallback。
            public static T Try<T>(string what, Func<T> action, T fallback) {
                try { return action(); }
                catch (Exception ex) { Warn(what, ex); return fallback; }
            }

            //供把 `catch { }` 直接改造成 `catch (Exception __ex) { Guard.Log("...", __ex); }` 用
            //（不改动原有 try 结构，改动面最小）。
            public static void Log(string what, Exception ex) { Warn(what, ex); }

            //同一个 what 的去重时间窗（秒）：避免每帧路径失败时刷屏
            private const float RepeatWindowSeconds = 5f;
            private static readonly Dictionary<string, float> _lastWarn = new Dictionary<string, float>();

            private static void Warn(string what, Exception ex) {
                try {
                    float now = Time.unscaledTime;
                    float last;
                    if (what != null && _lastWarn.TryGetValue(what, out last) && now - last < RepeatWindowSeconds) return;
                    if (what != null) _lastWarn[what] = now;
                    MainPlugin.ModLogger.LogWarning("[Guard] " + what + " 失败: " + (ex != null ? ex.Message : "未知错误"));
                } catch { }
            }
        }

	}
}
