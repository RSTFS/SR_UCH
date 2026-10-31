// 提示与日志门面：屏幕提示统一走 NotifyImpl -> ShowUserMessage（合并 + 降级），日志统一走 LogInfo/LogWarn/LogError。
// 内部不裸调 MainPlugin.ModLogger：极早期可能为 null，catch 里裸调会二次抛 NRE。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Notify（统一提示 / 值拼接 / 日志门面 / 屏幕合并与通道降级）

        // 统一提示：游戏原生 UserMessage（屏幕上那行字）+ BepInEx 日志。
        // Notify 纯文本 / Notify(文本, 值...) 文本 + 值 / NotifyT 双语 + 值 / NotifyTag 指定来源；实现只有 NotifyImpl 一份。
        public static void Notify(string text) { NotifyImpl(text, null); }
        public static void NotifyTag(string tag, string text) { NotifyImpl(text, tag); }
        public static void Notify(string text, params object[] args) { NotifyImpl(Cat(text, args), null); }
        public static void NotifyT(string zh, string en, params object[] args) { NotifyImpl(Cat(T(zh, en), args), null); }

        // 值拼接：null 视作空串（避免出现 "null"），其余走 ToString；公开给扩展模块复用。
        public static string Cat(string text, object[] args) {
            if (args == null || args.Length == 0) return text;
            StringBuilder sb = new StringBuilder(text != null ? text : "");
            for (int i = 0; i < args.Length; i++) sb.Append(args[i] != null ? args[i].ToString() : "");
            return sb.ToString();
        }

        // 日志门面：内部不裸调 MainPlugin.ModLogger（极早期可能为 null，catch 里裸调会二次抛 NRE）。
        // 参数用 object（与 BepInEx ILogger.LogInfo(object) 一致）：调用点会传异常对象。
        public static void LogInfo(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogInfo(m); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogWarn(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogWarning(m); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
        public static void LogError(object m) { try { if (MainPlugin.ModLogger != null) MainPlugin.ModLogger.LogError(m); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }

        // 屏幕合并：游戏 MessageComponent 只有一个 Text、没有队列（后发覆盖先发），
        // 故在 0.35 秒窗口内把多条合成多行（最多 3 行，再多写"等 N 条"）；只影响屏幕，日志每条独立。
        private static readonly List<string> _tipBatch = new List<string>();
        private static float _tipBatchUntil;

        // 通道降级：某些场景游戏没实例化消息 UI（Instance 非空但内部 UIMsg 为 null），
        // 已知不可用时只写日志、不再逐帧抛异常；场景一换自动重试恢复。
        private static bool _tipDown;
        private static string _tipDownScene;
        private static float _tipRetryAt;   // 降级后的下次重试时刻（60 秒一次）

        private static void NotifyImpl(string text, string tag) {
            try { ShowUserMessage(text); } catch (Exception __ex) { Guard.Log("提示(UserMessage)", __ex); }
            try { SR.LogInfo("[" + (string.IsNullOrEmpty(tag) ? "SR" : tag) + "] " + text); } catch (Exception __ex) { Guard.Log("NotifyImpl", __ex); }
        }

        // 把提示送到屏幕：短时合并 + 通道不可用时降级（只记日志）。
        private static void ShowUserMessage(string text) {
            if (string.IsNullOrEmpty(text)) return;
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            // 本场景已知不可用：跳过（日志照常）。但每 60 秒允许重试一次 ——
            // 以前只在换场景时恢复，长局里消息 UI 恢复可用后仍会一直静默（audit E-13）。
            if (_tipDown && scene == _tipDownScene) {
                if (Time.unscaledTime < _tipRetryAt) return;
                _tipRetryAt = Time.unscaledTime + 60f;
            }
            string show = text;
            try {
                float now = Time.unscaledTime;
                if (now <= _tipBatchUntil) {
                    // 同一次操作连报几条：合并成多行，避免互相顶掉
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
                // 显式走四参重载：二参 (string, bool) 会把提示绑到当前场景、固定 2 秒 / lo 优先级，
                // 场景一换提示就消失（audit 03-P0-01）。这里固定 2 秒、lo、且不绑场景。
                UserMessageManager.Instance.UserMessage(show, 2f, UserMessageManager.UserMsgPriority.lo, false);
                if (_tipDown) { _tipDown = false; _tipDownScene = null; }   // 调通了说明已恢复
            } catch (Exception __ex) {
                _tipDown = true; _tipDownScene = scene;
                Guard.Log("提示(UserMessage)", __ex);
            }
        }

}
}
