// UI 层级：统一 z-order，取代散落在各文件的魔数。
// 注意：新增层级必须在此登记；运行时 Register 会检测重复。
using System;
using System.Collections.Generic;

namespace SR_UCH {

    public enum UiLayer {
        Backdrop     = -2000,   // 浮窗背景遮罩
        Modal        = -1000,   // 浮窗本体
        Main         = 0,       // SR 主窗口
        Hud          = 1,       // 局内 HUD
        Toast        = 3,       // 通知浮层
        WorldOverlay = 100,     // 地图/世界空间叠加（画在主窗口之下）
    }

    public static class UiLayerRegistry {
        private static readonly Dictionary<int, string> _owners = new Dictionary<int, string>();
        private static readonly List<string> _conflicts = new List<string>();

        // 启动期调用；同一层被两个功能声明即记录冲突（不抛异常，进诊断报告）。
        public static void Register(string owner, UiLayer layer) {
            int v = (int)layer;
            string prev;
            if (_owners.TryGetValue(v, out prev) && prev != owner) {
                string s = owner + " 与 " + prev + " 同层 " + layer;
                if (!_conflicts.Contains(s)) _conflicts.Add(s);
            } else _owners[v] = owner;
        }
        public static IList<string> Conflicts { get { return new List<string>(_conflicts); } }
        public static int Count { get { return _owners.Count; } }
        public static void Reset() { _owners.Clear(); _conflicts.Clear(); }
    }
}
