// 契约层（拆分后的公共部分）：ITweak / IFeatureHost / ILanguagePack / Slots / 功能清单。
// 硬约定：SR_UCH\Contracts\ 下所有文件不得引用 SR 的其它文件，须能单独编译成程序集；
// 槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
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
    // 线程契约（audit item 12）：本文件里的静态集合（Slots._map / _frameObjCache / Ctl._contentCache）与
    // SR.* 分部里的静态缓存（_heightCache 等）全部"只允许主线程访问"。
    // 现状：所有读写都发生在 Unity 主线程（Awake / Update / OnGUI / 事件回调），因此无锁也正确；
    // 约定：任何异步线程（Task / 线程池 / 协程之外的计时器）都不得直接碰这些集合，
    //       需要跨线程时先 Marshal 回主线程，或把这些集合换成 ConcurrentDictionary。
    // 检查：打开 SetThreadCheck(true)（诊断模式）后，越线程访问会记一次警告，见 MainThreadCheck。
    public static class MainThread {
        private static int _mainId = -1;
        private static bool _on;
        private static int _warned;
        public static void Enable(bool on) {
            _on = on;
            if (_mainId < 0) { try { _mainId = System.Threading.Thread.CurrentThread.ManagedThreadId; } catch { _mainId = -1; } }
        }
        // 只在开启时比较一次线程 id；越线程时每类 what 只记一次，避免刷屏。
        public static void Assert(string what) {
            if (!_on || _mainId < 0) return;
            int id;
            try { id = System.Threading.Thread.CurrentThread.ManagedThreadId; } catch { return; }
            if (id == _mainId) return;
            if (_warned > 64) return;
            _warned++;
            // LogWarn 的实现本身就是 Guard 包裹（不会抛），这里不再额外 try/catch，
            // 免得又引入一处静默 catch（棘轮守卫会拦）。
            SRApi.LogWarn("[SR.Thread] 非主线程访问静态集合: " + what + "（线程 " + id + "，主线程 " + _mainId + "）");
        }
    }

    // 槽位总线：功能间通信只认 "名字 + 委托签名"，不认识双方类型；Require 返回 null 表示无人提供，调用方须自行降级。
    // Provide 为覆盖语义；硬约定：反射游戏私有成员须走 SR.RefField / SR.RefMethod 并经 SR.RefOverride 登记。
    public static class Slots {
        // 仅主线程访问（见文件头线程契约）；Provide/Require 都会 Assert 一次。
        private static readonly Dictionary<string, object> _map = new Dictionary<string, object>();
        //记录无人提供或类型不符的槽位名（最多 64 条，只记不同的名字），供自检界面显示。
        private static readonly Dictionary<string, int> _misses = new Dictionary<string, int>();
        private static int _missOverflow;   // 超过 64 条后的聚合计数（audit 02-P0-2）
        public static int MissCount { get { return _misses.Count; } }
        public static ICollection<string> MissNames { get { return _misses.Keys; } }

        private static void NoteMiss(string slot) {
            try {
                if (_misses.Count >= 64 && !_misses.ContainsKey(slot)) {
                    // 超过 64 条不再静默丢弃：聚合成一条可见记录（audit 02-P0-2），
                    // 否则自检页只看到前 64 个名字，后面的错误被完全隐藏。
                    _missOverflow++;
                    _misses["（另有 " + _missOverflow + " 个未列出的槽位）"] = _missOverflow;
                    if (_missOverflow == 1) {
                        try { SRApi.LogWarn("[Slots] 未提供/类型不符的槽位超过 64 条，多出的只做计数"); } catch { /* 日志失败不影响统计 */ }
                    }
                    return;
                }
                int c;
                _misses.TryGetValue(slot, out c);
                _misses[slot] = c + 1;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }

        public static void Provide(string slot, object impl) {
            MainThread.Assert("Slots.Provide");
            if (impl != null && !string.IsNullOrEmpty(slot)) { _map[slot] = impl; _misses.Remove(slot); }
        }
        // 注销自己提供的槽位（R2：ITweak 没有卸载，功能在关机/禁用时用它回收）。
        // 只删自己提供的那个实现，避免把别人后注册的槽位误删。
        public static void Retract(string slot, object expected) {
            MainThread.Assert("Slots.Retract");
            if (string.IsNullOrEmpty(slot)) return;
            object cur;
            if (!_map.TryGetValue(slot, out cur)) return;
            if (expected != null && !ReferenceEquals(cur, expected)) return;
            _map.Remove(slot);
        }
        public static T Require<T>(string slot) where T : class {
            MainThread.Assert("Slots.Require");
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

    // 契约版本（对应 SMAPI 的 minimumApiVersion）：核心只增不改地推进这个数字；
    // 功能用 [SrFeature] 的 MinContract 声明所需版本，核心低于它时记录"契约不兼容"。
    public static class SRContract {
        // Version：功能清单契约（ITweak/IFeatureHost/Slots）的版本；与 SR.ApiVersion（EX 接口版本）独立演进。
        public const int Version = 2;
    }

    // 跨功能共享的时间常量（audit P2⑧）：以前 45s / 25s 在 Level.* 与 BuildingPresets 各写一份，
    // 只靠注释互相提醒"与对方保持一致"。放在契约层后两边引用同一个值，改一处即同步。
    public static class SrTimeouts {
        public const float ReloadLockStaleSeconds = 45f;      // 关卡重载锁自愈阈值
        public const float PresetConsumeTimeoutSeconds = 25f; // 预设写进 levelPortalXml 后等游戏消费的硬上限
    }

    // 功能清单（对应 SMAPI 的 manifest.json）：写在 ITweak 实现类上。
    // 核心据此生成功能表、检查依赖与槽位冲突、排序侧栏；未声明的旧式功能用类型全名兜底。
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SrFeatureAttribute : Attribute {
        public string Id { get; private set; }        //稳定 id（日志/依赖用），如 "sr.chat"
        public string Name { get; private set; }      //显示名
        public string Version { get; private set; }   //功能自身版本，如 "1.0"
        public int Rank { get; set; }                 //侧栏顺序建议值（越小越前）
        public string[] DependsOn { get; set; }       //依赖的功能 id；缺失时记录并降级运行
        public string[] Provides { get; set; }        //本功能会写入的槽位名（冲突检测用）
        public int MinContract { get; set; }          //所需最低契约版本
        public int Modes { get; set; }                //允许的游戏模式位（SRApi.ModeMask 的整型值；0 = 不限）
        public SrFeatureAttribute(string id, string name, string version) {
            Id = id; Name = name; Version = version; Rank = 120; MinContract = SRContract.Version; Modes = 0;
        }
    }

    // 一条功能清单记录：核心在扫描 ITweak 时填充，界面/日志只读。
    public sealed class FeatureInfo {
        public string Id, Name, Version;
        public int Rank, MinContract, Modes;
        public string[] DependsOn, Provides;
        public Type Impl;
        public bool ManifestMissing;                  // true = 该类没有 [SrFeature]（旧式/第三方未声明）
        public bool ContractTooNew;                   // true = MinContract > 当前契约版本
        public readonly List<string> MissingDeps = new List<string>();
        public readonly List<string> ConflictSlots = new List<string>();   // 同一槽位被多个功能声明 Provide
        public override string ToString() {
            string s = Id + " " + Version;
            if (ManifestMissing) s += "（未声明清单）";
            if (ContractTooNew) s += "（要求契约 v" + MinContract + "，当前 v" + SRContract.Version + "）";
            if (MissingDeps.Count > 0) s += "（缺依赖: " + string.Join(", ", MissingDeps.ToArray()) + "）";
            if (ConflictSlots.Count > 0) s += "（槽位冲突: " + string.Join(", ", ConflictSlots.ToArray()) + "）";
            if (Modes != 0) s += "（模式受限）";
            return s;
        }
    }

    // 功能注册表（SMAPI 的 mod registry 对应物）：核心写，自检页/日志读。
    public static class Features {
        private static readonly List<FeatureInfo> _all = new List<FeatureInfo>();
        private static readonly Dictionary<string, FeatureInfo> _byId = new Dictionary<string, FeatureInfo>();
        public static IList<FeatureInfo> All { get { return _all; } }
        public static int Count { get { return _all.Count; } }
        public static int ManifestMissingCount { get { int n = 0; for (int i = 0; i < _all.Count; i++) if (_all[i].ManifestMissing) n++; return n; } }
        public static int ProblemCount { get { int n = 0; for (int i = 0; i < _all.Count; i++) if (_all[i].ContractTooNew || _all[i].MissingDeps.Count > 0 || _all[i].ConflictSlots.Count > 0) n++; return n; } }
        public static FeatureInfo Find(string id) {
            if (string.IsNullOrEmpty(id)) return null;
            FeatureInfo fi; return _byId.TryGetValue(id, out fi) ? fi : null;
        }
        // 登记一条（id 冲突时用"id#类型名"去重，避免同一 id 被两个类占用而覆盖）
        public static void Register(FeatureInfo fi) {
            if (fi == null || string.IsNullOrEmpty(fi.Id)) return;
            if (_byId.ContainsKey(fi.Id)) fi.Id = fi.Id + "#" + (fi.Impl != null ? fi.Impl.Name : "?");
            _all.Add(fi); _byId[fi.Id] = fi;
        }
        // 从类型读清单；没有 [SrFeature] 时用类型全名兜底并标记 ManifestMissing
        public static FeatureInfo FromType(Type t) {
            if (t == null) return null;
            FeatureInfo fi = new FeatureInfo { Id = t.FullName, Name = t.Name, Version = "0", Rank = 120, MinContract = 0, Impl = t, ManifestMissing = true };
            try {
                object[] attrs = t.GetCustomAttributes(typeof(SrFeatureAttribute), false);
                if (attrs != null && attrs.Length > 0) {
                    SrFeatureAttribute a = (SrFeatureAttribute)attrs[0];
                    fi.Id = a.Id; fi.Name = a.Name; fi.Version = a.Version; fi.Rank = a.Rank;
                    fi.MinContract = a.MinContract; fi.DependsOn = a.DependsOn; fi.Provides = a.Provides;
                    fi.Modes = a.Modes;
                    fi.ManifestMissing = false;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            fi.ContractTooNew = fi.MinContract > SRContract.Version;
            return fi;
        }
        // 扫描所有已加载程序集里的 ITweak 实现（内置 + 外部 + EX），重建功能表并解析依赖
        public static void Rebuild() {
            _all.Clear(); _byId.Clear();
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++) {
                Type[] types;
                try { types = asms[i].GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                for (int j = 0; j < types.Length; j++) {
                    Type t = types[j];
                    if (t == null || t.IsInterface || t.IsAbstract) continue;
                    try { if (!typeof(ITweak).IsAssignableFrom(t)) continue; } catch { continue; }
                    Register(FromType(t));
                }
            }
            for (int i = 0; i < _all.Count; i++) {
                FeatureInfo fi = _all[i]; fi.MissingDeps.Clear(); fi.ConflictSlots.Clear();
                if (fi.DependsOn == null) continue;
                for (int k = 0; k < fi.DependsOn.Length; k++)
                    if (!string.IsNullOrEmpty(fi.DependsOn[k]) && !_byId.ContainsKey(fi.DependsOn[k])) fi.MissingDeps.Add(fi.DependsOn[k]);
            }
            // Provides 冲突：同一个槽位被两个功能声明提供 -> 双方都算问题（以前只靠约定，冲突检测形同虚设）。
            Dictionary<string, List<FeatureInfo>> owners = new Dictionary<string, List<FeatureInfo>>();
            for (int i = 0; i < _all.Count; i++) {
                FeatureInfo fi = _all[i];
                if (fi.Provides == null) continue;
                for (int k = 0; k < fi.Provides.Length; k++) {
                    string slot = fi.Provides[k];
                    if (string.IsNullOrEmpty(slot)) continue;
                    List<FeatureInfo> list;
                    if (!owners.TryGetValue(slot, out list)) { list = new List<FeatureInfo>(); owners[slot] = list; }
                    if (!list.Contains(fi)) list.Add(fi);
                }
            }
            foreach (KeyValuePair<string, List<FeatureInfo>> kv in owners) {
                if (kv.Value.Count < 2) continue;
                for (int i = 0; i < kv.Value.Count; i++)
                    if (!kv.Value[i].ConflictSlots.Contains(kv.Key)) kv.Value[i].ConflictSlots.Add(kv.Key);
            }
        }
        // 一行摘要：合同 vN / 功能 M 个 / 问题 K 条，问题明细跟在后面
        public static string Describe() {
            string head = "[SR.Feat] 契约 v" + SRContract.Version + " · 功能 " + _all.Count + " 个";
            if (ManifestMissingCount > 0) head += " · 未声明清单 " + ManifestMissingCount + " 个";
            if (ProblemCount > 0) head += " · 有问题 " + ProblemCount + " 个";
            string detail = "";
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].ContractTooNew || _all[i].MissingDeps.Count > 0 || _all[i].ConflictSlots.Count > 0 || _all[i].ManifestMissing)
                    detail += "\n    - " + _all[i].ToString();
            return head + detail;
        }
    }
    // API 门面（SMAPI 的 SMAPI.Mod* 助手对应物）：功能层只允许用这里的成员。
    // 契约层不引用实现层，日志出口由核心在启动时注入（SRApi.SetLogSinks）。
}
