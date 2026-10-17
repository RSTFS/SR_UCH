// 契约层：ITweak / IFeatureHost / ILanguagePack / Slots，第三方功能只依赖本文件。
// 硬约定：本文件不得引用 SR 的其它文件，须能单独编译成程序集；槽位只传 BCL / 引擎 / 游戏类型。
using System;
using System.Collections.Generic;
using System.Reflection;
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

    // 契约版本（对应 SMAPI 的 minimumApiVersion）：核心只增不改地推进这个数字；
    // 功能用 [SrFeature] 的 MinContract 声明所需版本，核心低于它时记录"契约不兼容"。
    public static class SRContract {
        public const int Version = 2;
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
        public SrFeatureAttribute(string id, string name, string version) {
            Id = id; Name = name; Version = version; Rank = 120; MinContract = SRContract.Version;
        }
    }

    // 一条功能清单记录：核心在扫描 ITweak 时填充，界面/日志只读。
    public sealed class FeatureInfo {
        public string Id, Name, Version;
        public int Rank, MinContract;
        public string[] DependsOn, Provides;
        public Type Impl;
        public bool ManifestMissing;                  // true = 该类没有 [SrFeature]（旧式/第三方未声明）
        public bool ContractTooNew;                   // true = MinContract > 当前契约版本
        public readonly List<string> MissingDeps = new List<string>();
        public override string ToString() {
            string s = Id + " " + Version;
            if (ManifestMissing) s += "（未声明清单）";
            if (ContractTooNew) s += "（要求契约 v" + MinContract + "，当前 v" + SRContract.Version + "）";
            if (MissingDeps.Count > 0) s += "（缺依赖: " + string.Join(", ", MissingDeps.ToArray()) + "）";
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
        public static int ProblemCount { get { int n = 0; for (int i = 0; i < _all.Count; i++) if (_all[i].ContractTooNew || _all[i].MissingDeps.Count > 0) n++; return n; } }
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
                    fi.ManifestMissing = false;
                }
            } catch { }
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
                FeatureInfo fi = _all[i]; fi.MissingDeps.Clear();
                if (fi.DependsOn == null) continue;
                for (int k = 0; k < fi.DependsOn.Length; k++)
                    if (!string.IsNullOrEmpty(fi.DependsOn[k]) && !_byId.ContainsKey(fi.DependsOn[k])) fi.MissingDeps.Add(fi.DependsOn[k]);
            }
        }
        // 一行摘要：合同 vN / 功能 M 个 / 问题 K 条，问题明细跟在后面
        public static string Describe() {
            string head = "[SR.Feat] 契约 v" + SRContract.Version + " · 功能 " + _all.Count + " 个";
            if (ManifestMissingCount > 0) head += " · 未声明清单 " + ManifestMissingCount + " 个";
            if (ProblemCount > 0) head += " · 有问题 " + ProblemCount + " 个";
            string detail = "";
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].ContractTooNew || _all[i].MissingDeps.Count > 0 || _all[i].ManifestMissing)
                    detail += "\n    - " + _all[i].ToString();
            return head + detail;
        }
    }
    // API 门面（SMAPI 的 SMAPI.Mod* 助手对应物）：功能层只允许用这里的成员。
    // 契约层不引用实现层，日志出口由核心在启动时注入（SRApi.SetLogSink）。
    public static class SRApi {
        private static Action<string> _warn;
        // 核心启动时注入：把契约层的告警接到实现的日志系统（无注入时静默丢弃，不影响功能）。
        public static void SetLogSink(Action<string> warn) { _warn = warn; }
        internal static void Warn(string msg) { try { Action<string> w = _warn; if (w != null) w(msg); } catch { } }

        // 统一带日志的 try / catch 包装：同一条 what 5 秒内只记一次，每帧路径上也安全。
        public static class Guard {
            private static readonly Dictionary<string, int> _lastWarn = new Dictionary<string, int>();
            public static string LastWhat, LastError;
            public static int WarnCount;

            public static void Try(string what, Action action) {
                try { action(); } catch (Exception ex) { Warn(what, ex); }
            }
            public static T Try<T>(string what, Func<T> action, T fallback) {
                try { return action(); } catch (Exception ex) { Warn(what, ex); return fallback; }
            }
            public static void Log(string what, Exception ex) { Warn(what, ex); }

            private static void Warn(string what, Exception ex) {
                try {
                    int now = Environment.TickCount;
                    int last;
                    if (what != null && _lastWarn.TryGetValue(what, out last) && unchecked((uint)(now - last)) < 5000u) return;
                    if (what != null) _lastWarn[what] = now;
                    LastWhat = what;
                    LastError = ex != null ? (ex.GetType().Name + ": " + ex.Message) : "未知错误";
                    WarnCount++;
                    SRApi.Warn("[Guard] " + what + " 失败: " + (ex != null ? ex.Message : "未知错误"));
                } catch (Exception __ex) {
                    try { LastWhat = what; LastError = "Warn 自身失败: " + __ex.GetType().Name; } catch { }
                }
            }
        }
    }

}
