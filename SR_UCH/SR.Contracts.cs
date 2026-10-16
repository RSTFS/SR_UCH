// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：本文件不得引用 SR 的其它文件，须能单独编译成程序集；槽位只传 BCL / 引擎 / 游戏类型。
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SR_UCH {
    //功能统一契约：实现本接口即被 MainPlugin 反射发现并调用 Initialize。
    public interface ITweak {
        void Initialize(IFeatureHost host);
    }

    //功能初始化时注入的宿主：Config 读写自身配置，Logger 写 BepInEx 日志。
    //不含 Harmony —— 功能自行 new Harmony(id)。
    public interface IFeatureHost {
        ConfigFile Config { get; }
        ManualLogSource Logger { get; }
    }

    // 外部语言包契约：一个 DLL = 一门语言。类库引用 SR_UCH.dll 实现本接口，DLL 放入 BepInEx\plugins\。
    // SR 启动时扫描 plugins 目录注册实现类，界面语言下拉框随即增加选项（写入 cfg 持久保存）。
    public interface ILanguagePack {
        //稳定 id（写入 cfg，如 "fr"），不要使用显示名。
        string Id { get; }
        //下拉框显示名（如 "Français"）。
        string DisplayName { get; }
        //未收录文案的回退语言："English"（默认）或 "中文"。
        string FallbackId { get; }
        //词条表：key = SR 源码中文原文（也接受英文原名 / 枚举名 / 场景名 / KeyCode 名），value = 译文；
        //未收录的 key 按 FallbackId 回退，可增量补全。
        IDictionary<string, string> Map { get; }
    }
    // 槽位总线：功能间通信只认 "名字 + 委托签名"，不认识双方类型；Require 返回 null 表示无人提供，调用方须自行降级。
    // Provide 为覆盖语义；硬约定：反射游戏私有成员须走 SR.RefField / SR.RefMethod 并经 SR.RefOverride 登记。
    public static class Slots {
        private static readonly Dictionary<string, object> _map = new Dictionary<string, object>();
        //记录无人提供或类型不符的槽位名（最多 64 条，只记不同的名字），供自检界面显示。
        private static readonly Dictionary<string, int> _misses = new Dictionary<string, int>();
        public static int MissCount { get { return _misses.Count; } }
        public static ICollection<string> MissNames { get { return _misses.Keys; } }

        private static void NoteMiss(string slot) {
            try {
                if (_misses.Count >= 64 && !_misses.ContainsKey(slot)) return;
                int c;
                _misses.TryGetValue(slot, out c);
                _misses[slot] = c + 1;
            } catch { }
        }

        public static void Provide(string slot, object impl) {
            if (impl != null && !string.IsNullOrEmpty(slot)) { _map[slot] = impl; _misses.Remove(slot); }
        }
        public static T Require<T>(string slot) where T : class {
            if (string.IsNullOrEmpty(slot)) return null;
            object o;
            if (_map.TryGetValue(slot, out o)) {
                T typed = o as T;
                if (typed != null) return typed;
                NoteMiss(slot + "（类型不符）");   //已登记但类型不符
                return null;
            }
            NoteMiss(slot);
            return null;
        }
        //自检用：当前登记的槽位数量
        public static int Count { get { return _map.Count; } }
        public static ICollection<string> Names { get { return _map.Keys; } }
    }
}
