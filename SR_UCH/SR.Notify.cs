// 提示与日志门面：屏幕提示走 NotifyImpl → ShowUserMessage（合并 + 降级）；日志走 LogXxx。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Notify（统一提示 / 值拼接 / 日志 / 合并与降级）

        // 优先交通知中心钩子（可画动作按钮 / 精确分级），否则退回普通提示。
        internal static void NotifyEx(SRApi.NotifyArgs a) {
            try {
                SRApi.NotifyExFn hook = SrCaps.NotifyHub2;
                if (hook != null && hook(a)) return;
                Notify(a != null ? a.Text : "");
            } catch (Exception __ne) { Guard.Log("通知中心/NotifyEx 转发", __ne); }
        }

        public static void Notify(string text) { NotifyImpl(text, null); }
        public static void NotifyTag(string tag, string text) { NotifyImpl(text, tag); }
        public static void Notify(string text, params object[] args) { NotifyImpl(Cat(text, args), null); }
        public static void NotifyT(string zh, string en, params object[] args) { NotifyImpl(Cat(T(zh, en), args), null); }

        // 值拼接：null 视作空串；公开给外部 DLL 复用。
        public static string Cat(string text, object[] args) {
            if (args == null || args.Length == 0) return text;
            StringBuilder sb = new StringBuilder(text != null ? text : "");
            for (int i = 0; i < args.Length; i++) sb.Append(args[i] != null ? args[i].ToString() : "");
            return sb.ToString();
        }

        // 日志门面：不裸调 ModLogger（极早期可能为 null，catch 里裸调会二次抛 NRE）。
        public static void LogInfo(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogInfo(m); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogWarn(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogWarning(m); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogError(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogError(m); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }

        // 屏幕合并：MessageComponent 只有一个 Text、无队列，故 0.35 秒内合成多行（最多 3 行）。
        private static readonly List<string> _tipBatch = new List<string>();
        private static float _tipBatchUntil;

        // 通道降级：内部 UIMsg 为 null 时只写日志；换场景或每 60 秒重试恢复。
        private static bool _tipDown;
        private static string _tipDownScene;
        private static float _tipRetryAt;   // 下次重试时刻（60 秒一次）

        // 通知治理钩子（可选）：true = 治理中心负责显示，false / 无提供者照旧。
        private static Func<string, string, bool> _notifyHook;
        private static float _notifyHookNext;

        private static Func<string, string, bool> NotifyHook() {
            // 每 2 秒复核：通知中心 Shutdown 会清空能力容器，旧缓存必须失效。
            if (Time.unscaledTime < _notifyHookNext) return _notifyHook;
            _notifyHookNext = Time.unscaledTime + 2f;
            _notifyHook = SrCaps.NotifyHook;
            return _notifyHook;
        }

        private static void NotifyImpl(string text, string tag) {
            bool handled = false;
            try {
                Func<string, string, bool> hook = NotifyHook();
                if (hook != null) handled = hook(text, tag ?? "");
            } catch (Exception __ex) { Guard.Log("提示(治理钩子)", __ex); }
            if (!handled) {
                try { ShowUserMessage(text); } catch (Exception __ex) { Guard.Log("提示(UserMessage)", __ex); }
            }
            try { SR.LogInfo("[" + (string.IsNullOrEmpty(tag) ? "SR" : tag) + "] " + text); } catch (Exception __ex) { Guard.Log("NotifyImpl", __ex); }
        }

        private static void ShowUserMessage(string text) {
            if (string.IsNullOrEmpty(text)) return;
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            // 本场景已知不可用则跳过（日志照常），每 60 秒重试一次。
            if (_tipDown && scene == _tipDownScene) {
                if (Time.unscaledTime < _tipRetryAt) return;
                _tipRetryAt = Time.unscaledTime + 60f;
            }
            string show = text;
            try {
                float now = Time.unscaledTime;
                if (now <= _tipBatchUntil) {
                    // 同一次操作连报几条：合并成多行，避免互相顶掉。
                    if (!_tipBatch.Contains(text)) _tipBatch.Add(text);
                    if (_tipBatch.Count > 3) {
                        show = _tipBatch[0] + "\n" + _tipBatch[1] + "\n" + _tipBatch[2]
                             + "\n…等 " + _tipBatch.Count + " 条";
                    } else {
                        show = string.Join("\n", _tipBatch.ToArray());
                    }
                } else {
                    _tipBatch.Clear();
                    _tipBatch.Add(text);
                }
                _tipBatchUntil = now + 0.35f;
            } catch { show = text; }   // 拿不到时间就退回单条
            try {
                // 必须走四参重载：二参会把提示绑到当前场景，换场景即消失并固定 2 秒 / lo。
                UserMessageManager.Instance.UserMessage(show, 2f, UserMessageManager.UserMsgPriority.lo, false);
                if (_tipDown) { _tipDown = false; _tipDownScene = null; }   // 调通即恢复
            } catch (Exception __ex) {
                _tipDown = true; _tipDownScene = scene;
                Guard.Log("提示(UserMessage)", __ex);
            }
        }

}
}
