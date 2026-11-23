// 委托订阅撤销登记：Initialize 作用域内的订阅须配套登记 undo，卸载时逆序执行。
// 注意：undo 必须幂等；进程级订阅（程序集解析 / OnEnable-OnDisable 对称）不登记。
using System;

namespace SR_UCH {

    public static class Sub {
        private static readonly System.Collections.Generic.List<Action> _global = new System.Collections.Generic.List<Action>();
        public static int GlobalCount { get { return _global.Count; } }
        public static void Revoke(Action undo) {
            if (undo == null) return;
            if (FeatureRuntime.Current != null) {
                FeatureRuntime.RecordRevoke(undo);
                FeatureRuntime.RecordSubscription("event");
            } else {
                _global.Add(undo);   // 核心层 / 启动期订阅：随进程关机统一撤销
            }
        }
        public static void DisposeAll() {
            for (int i = _global.Count - 1; i >= 0; i--) { try { _global[i](); } catch (Exception) { } }
            _global.Clear();
        }
        public static void Add(Action subscribe, Action undo) {
            if (subscribe != null) subscribe();
            Revoke(undo);
        }
    }
}
