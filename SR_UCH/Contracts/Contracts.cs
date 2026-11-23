// 契约层：功能接口、宿主、强类型能力容器、功能清单、作用域注册表。
// 注意：本目录文件不得引用 SR 的实现文件，须能脱离游戏单独编译。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using BepInEx.Logging;

namespace SR_UCH {
    // 功能统一契约：实现本接口即被 MainPlugin 反射发现并调用 Initialize。
    public interface ITweak {
        void Initialize(IFeatureHost host);
    }

    // 功能初始化时注入的宿主：Config 读写自身配置，Logger 写 BepInEx 日志；不含 Harmony。
    public interface IFeatureHost {
        ConfigFile Config { get; }
        ManualLogSource Logger { get; }
    }

    // 外部语言包契约：一个 DLL = 一门语言；类库引用 SR_UCH.dll 实现本接口后放入 BepInEx\plugins\，启动时自动注册。
    public interface ILanguagePack {
        string Id { get; }
        string DisplayName { get; }
        string FallbackId { get; }
        // 词条表：key = 稳定 ID（@id，见 SrLocIds）；value = 译文。
        IDictionary<string, string> Map { get; }
    }
    // 线程契约：静态集合与 SR.* 分部里的静态缓存只允许主线程访问；异步线程须先 Marshal 回主线程。
    // 诊断：打开 SetThreadCheck(true) 后，越线程访问会记一次警告。
    public static class MainThread {
        private static int _mainId = -1;
        private static bool _on;
        private static int _warned;
        public static void Enable(bool on) {
            _on = on;
            if (_mainId < 0) { try { _mainId = System.Threading.Thread.CurrentThread.ManagedThreadId; } catch { _mainId = -1; } }
        }
        public static void Assert(string what) {
            if (!_on || _mainId < 0) return;
            int id;
            try { id = System.Threading.Thread.CurrentThread.ManagedThreadId; } catch { return; }
            if (id == _mainId) return;
            if (_warned > 64) return;
            _warned++;
            // LogWarn 本身已由 Guard 包裹（不会抛），这里不再额外 try/catch（棘轮守卫会拦）。
            SRApi.LogWarn("[SR.Thread] 非主线程访问静态集合: " + what + "（线程 " + id + "，主线程 " + _mainId + "）");
        }
    }

    // 单 DLL 内的强类型能力容器：功能只依赖契约层，不互引、不按名查表。
    // 提供者赋值并用 FeatureRuntime.RecordRevoke 登记撤销；消费者直接读，null 即降级。
    // 含游戏类型的委托用 System.Delegate 承载，契约层仍可脱离游戏独立编译。
    public static class SrCaps {
        public static Func<bool> LevelReloadBusy;              // Level 提供，布局预设读取
        public static Func<bool> PresetsReloadBusy;            // 布局预设提供，Level 读取
        public static Func<string, string, bool> NotifyHook;   // 通知中心提供，核心惰性读取
        public static SRApi.NotifyExFn NotifyHub2;             // 通知中心提供，NotifyEx 读取
        public static Func<bool> AllowClientsOn;               // 可选：外部 DLL 可赋值放行房客删除
        public static Delegate AttributeToHost;                // Action<List<Placeable>>：快照方块归属房主
        public static object SelfTestToken;                    // 主动自检往返用
    }

    // 契约版本：核心只增不改地推进；功能用 [SrFeature].MinContract 声明所需版本，核心低于它则记录“契约不兼容”。
    public static class SRContract {
        // Version：功能清单契约的破坏性基线，与 SR.ApiVersion 独立演进。
        public const int Version = 2;
        // ApiLevel：SRApi 门面 API 的 additive 等级，每新增一个 public 成员 +1；删除/改签名必须抬 Version。
        public const int ApiLevel = 16;  // 16 起有 FeatureRuntime 组合根（作用域 / 幂等 / 自动撤销）
    }

        // 跨功能共享的时间常量：Level.* 与 BuildingPresets 共用同一份值，改一处即同步。
    public static class SrTimeouts {
        public const float ReloadLockStaleSeconds = 45f;      // 关卡重载锁自愈阈值
        public const float PresetConsumeTimeoutSeconds = 25f; // 预设写进 levelPortalXml 后等游戏消费的硬上限
    }

    // 功能清单：写在 ITweak 实现类上；核心据此生成功能表、检查依赖与槽位冲突、排序侧栏。
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SrFeatureAttribute : Attribute {
        public string Id { get; private set; }        // 稳定 id（日志/依赖用），如 "sr.chat"
        public string Name { get; private set; }      // 显示名
        public string Version { get; private set; }   // 功能自身版本，如 "1.0"
        public int Rank { get; set; }                 // 侧栏顺序建议值（越小越前）
        public string[] DependsOn { get; set; }       // 依赖的功能 id；缺失时记录并降级运行
        public string[] Provides { get; set; }        // 本功能会写入的槽位名（冲突检测用）
        public int MinContract { get; set; }          // 所需最低破坏性契约版本
        public int MinApiLevel { get; set; }          // 所需最低 additive API 等级
        public int Modes { get; set; }                // 允许的游戏模式位（0 = 不限）
        public SrFeatureAttribute(string id, string name, string version) {
            Id = id; Name = name; Version = version; Rank = 120; MinContract = SRContract.Version; MinApiLevel = 0; Modes = 0;
        }
    }

    // 一条功能清单记录：核心扫描 ITweak 时填充，界面/日志只读。
    public sealed class FeatureInfo {
        public string Id, Name, Version;
        public int Rank, MinContract, MinApiLevel, Modes;
        public string[] DependsOn, Provides;
        public Type Impl;
        public bool ManifestMissing;                  // true = 该类没有 [SrFeature]；本体已退出功能表，此分支只服务外部 DLL 里未声明的 ITweak
        public bool ContractTooNew;                   // true = MinContract > 当前契约版本
        public readonly List<string> MissingDeps = new List<string>();
        public readonly List<string> ConflictSlots = new List<string>();   // 同一槽位被多个功能声明 Provide
        public override string ToString() {
            string s = Id + " " + Version;
            if (ManifestMissing) s += "（未声明清单）";
            if (ContractTooNew) s += "（要求契约 v" + MinContract + " / API " + MinApiLevel
                + "，当前 v" + SRContract.Version + " / API " + SRContract.ApiLevel + "）";
            if (MissingDeps.Count > 0) s += "（缺依赖: " + string.Join(", ", MissingDeps.ToArray()) + "）";
            if (ConflictSlots.Count > 0) s += "（槽位冲突: " + string.Join(", ", ConflictSlots.ToArray()) + "）";
            if (Modes != 0) s += "（模式受限）";
            return s;
        }
    }

    // 功能注册表：核心写，自检页/日志读。
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
        // 登记一条（id 冲突时用 "id#类型名" 去重，避免同一 id 被两个类占用而覆盖）。
        // 功能表版本：表内容每变一次就自增，外部缓存（如模式门控）据此失效。
        private static int _version;
        public static int Version { get { return _version; } }

        public static void Register(FeatureInfo fi) {
            if (fi == null || string.IsNullOrEmpty(fi.Id)) return;
            _version++;
            if (_byId.ContainsKey(fi.Id)) fi.Id = fi.Id + "#" + (fi.Impl != null ? fi.Impl.Name : "?");
            _all.Add(fi); _byId[fi.Id] = fi;
        }
        // 从类型读清单；没有 [SrFeature] 时用类型全名兜底并标记 ManifestMissing。
        // 内置 28 个功能全部有声明，兜底只在扫描外部 DLL 时触发（tests/Tests.cs 有覆盖）。
        public static FeatureInfo FromType(Type t) {
            if (t == null) return null;
            FeatureInfo fi = new FeatureInfo { Id = t.FullName, Name = t.Name, Version = "0", Rank = 120, MinContract = 0, Impl = t, ManifestMissing = true };
            try {
                object[] attrs = t.GetCustomAttributes(typeof(SrFeatureAttribute), false);
                if (attrs != null && attrs.Length > 0) {
                    SrFeatureAttribute a = (SrFeatureAttribute)attrs[0];
                    fi.Id = a.Id; fi.Name = a.Name; fi.Version = a.Version; fi.Rank = a.Rank;
                    fi.MinContract = a.MinContract; fi.MinApiLevel = a.MinApiLevel; fi.DependsOn = a.DependsOn; fi.Provides = a.Provides;
                    fi.Modes = a.Modes;
                    fi.ManifestMissing = false;
                }
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            fi.ContractTooNew = fi.MinContract > SRContract.Version || fi.MinApiLevel > SRContract.ApiLevel;
            return fi;
        }
        // 扫描所有已加载程序集里的 ITweak 实现，重建功能表并解析依赖。
        public static void Rebuild() {
            _all.Clear(); _byId.Clear(); _version++;
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
            // Provides 冲突：同一槽位被两个功能声明提供时双方都算问题。
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
        // 一行摘要：契约 vN / 功能 M 个 / 问题 K 条，问题明细跟在后面。
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

    // 可选生命周期：组合根在卸载时调用；未实现时只撤销自动登记的资源。
    // 注意：Shutdown 必须幂等，且不得再注册新资源。
    public interface ITweakLifecycle {
        void Shutdown();
    }

    // 一个功能在 Initialize 期间登记的资源账本。
    public sealed class FeatureLease {
        public string Id;
        public string TypeName;
        public bool InitOk;
        public object Instance;
        public readonly List<string> Ticks = new List<string>();
        public readonly List<string> Shutdowns = new List<string>();
        public readonly List<string> Pages = new List<string>();
        public readonly List<string> Navs = new List<string>();
        public readonly List<string> Hotkeys = new List<string>();
        public readonly List<Action> Anonymous = new List<Action>();
        public readonly List<Func<float, bool>> Wheels = new List<Func<float, bool>>();
        // 账本外注册（覆盖层 / 相机 / 场景 / 收起 / UI 阻塞 / 委托订阅）统一以撤销动作登记。
        // 注意：撤销必须幂等；卸载时逆序执行，执行后清空。
        public readonly List<Action> Revokes = new List<Action>();
        public readonly List<string> Subscriptions = new List<string>();
        // 槽位账本：本功能占用的槽位名，以及 Require 过但尚未满足的依赖（卸载日志与排障用）。
        public readonly List<string> Slots = new List<string>();
        public readonly List<string> PendingRequires = new List<string>();
        public Func<bool> Gate;
        public bool GateDeclared;
        public int Total {
            get { return Ticks.Count + Shutdowns.Count + Pages.Count + Navs.Count + Hotkeys.Count
                + Anonymous.Count + Wheels.Count + Revokes.Count + Subscriptions.Count
                + Slots.Count + PendingRequires.Count; }
        }
    }

    // 功能组合根：Initialize 期间开一个作用域，注册自动归属；卸载时按账本撤销。
    // 注意：只跑主线程；TryInit 按 Type 幂等，合并 / 拆分 / 外部三条装载路径共用它。
    public static class FeatureRuntime {
        private static readonly Dictionary<string, FeatureLease> _leases = new Dictionary<string, FeatureLease>();
        private static readonly HashSet<Type> _types = new HashSet<Type>();
        private static readonly List<string> _order = new List<string>();
        // 用 List 当栈：Stack<T> 在部分 .NET Framework 宿主上无法从 mscorlib 解析，
        // 会让 FeatureRuntime 的静态初始化直接抛 TypeInitializationException（契约层全废）。
        private static readonly List<FeatureLease> _stack = new List<FeatureLease>();
        private static Action<FeatureLease> _revoke;

        public static FeatureLease Current { get { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; } }
        public static string CurrentId { get { FeatureLease l = Current; return l != null ? l.Id : ""; } }
        public static int Count { get { return _leases.Count; } }
        public static int TypeCount { get { return _types.Count; } }
        public static IList<FeatureLease> All { get { return new List<FeatureLease>(_leases.Values); } }

        public static void SetRevokeSink(Action<FeatureLease> revoke) { _revoke = revoke; }

        // 登记功能实例：实现了 ITweakLifecycle 的实例会在卸载时收到 Shutdown。
        public static void SetInstance(object instance) { FeatureLease l = Cur(); if (l != null) l.Instance = instance; }

        // 幂等初始化：同一 Type 只跑一次；异常原样抛给调用方记录。
        public static bool TryInit(Type t, string id, Action init) {
            if (t == null || init == null || _types.Contains(t)) return false;
            _types.Add(t);
            string key = string.IsNullOrEmpty(id) ? t.FullName : id;
            if (_leases.ContainsKey(key)) key = key + "#" + t.Name;
            FeatureLease lease = new FeatureLease { Id = key, TypeName = t.FullName };
            _leases[key] = lease;
            _order.Add(key);
            _stack.Add(lease);
            try { init(); lease.InitOk = true; }
            finally { if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1); }
            return true;
        }

        private static void AddTo(List<string> list, string s) {
            if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s);
        }
        private static FeatureLease Cur() { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; }

        public static void RecordTick(string name) { FeatureLease l = Cur(); if (l != null) AddTo(l.Ticks, name); }
        public static void RecordShutdown(string name) { FeatureLease l = Cur(); if (l != null) AddTo(l.Shutdowns, name); }
        public static void RecordPage(string name) { FeatureLease l = Cur(); if (l != null) AddTo(l.Pages, name); }
        public static void RecordNav(string name) { FeatureLease l = Cur(); if (l != null) AddTo(l.Navs, name); }
        public static void RecordHotkey(string id) { FeatureLease l = Cur(); if (l != null) AddTo(l.Hotkeys, id); }
        public static void RecordSlot(string name, object owner) {
            FeatureLease l = Cur(); if (l != null) AddTo(l.Slots, name);
        }
        // ok=false 表示该依赖当前未满足，留在待满足账本里。
        public static void NoteRequire(string name, bool ok) {
            FeatureLease l = Cur();
            if (l == null || ok) return;
            AddTo(l.PendingRequires, name);
        }
        public static void RecordAnonymous(Action a) { FeatureLease l = Cur(); if (l != null && a != null && !l.Anonymous.Contains(a)) l.Anonymous.Add(a); }
        public static void RecordWheel(Func<float, bool> w) { FeatureLease l = Cur(); if (l != null && w != null && !l.Wheels.Contains(w)) l.Wheels.Add(w); }
        public static void RecordRevoke(Action a) { FeatureLease l = Cur(); if (l != null && a != null && !l.Revokes.Contains(a)) l.Revokes.Add(a); }
        public static void RecordSubscription(string name) { FeatureLease l = Cur(); if (l != null && !l.Subscriptions.Contains(name)) l.Subscriptions.Add(name); }

        // 可选门控：功能声明自己的 Enabled；未声明一律放行。
        // 门控改为显式声明；未声明的功能在诊断报告里可见，不再靠注释纪律。
        public static void SetGate(Func<bool> gate) {
            FeatureLease l = Cur();
            if (l != null) { l.Gate = gate; l.GateDeclared = true; }
        }
        public static int TotalRevokes() {
            int n = 0; foreach (FeatureLease l in _leases.Values) n += l.Revokes.Count; return n;
        }
        public static int TotalSubscriptions() {
            int n = 0; foreach (FeatureLease l in _leases.Values) n += l.Subscriptions.Count; return n;
        }
                public static int UndeclaredGates() {
            int n = 0;
            foreach (FeatureLease l in _leases.Values) if (l.Gate == null) n++;
            return n;
        }
        public static bool GateAllows(string owner) {
            if (string.IsNullOrEmpty(owner)) return true;
            FeatureLease l;
            if (!_leases.TryGetValue(owner, out l) || l.Gate == null) return true;
            try { return l.Gate(); }
            catch (Exception e) { SRApi.LogWarn("[FeatureRuntime] 门控 " + owner + " 异常: " + e.Message); return true; }
        }

        // 撤销单个功能并把 Type 放回可初始化状态；供运行时禁用 / 重载使用。
        public static bool Dispose(string id) {
            FeatureLease lease;
            if (string.IsNullOrEmpty(id) || !_leases.TryGetValue(id, out lease)) return false;
            try {
                ITweakLifecycle life = lease.Instance as ITweakLifecycle;
                if (life != null) life.Shutdown();
            } catch (Exception e) { SRApi.LogWarn("[FeatureRuntime] " + id + " Shutdown 失败: " + e.Message); }
            try { if (_revoke != null) _revoke(lease); }
            catch (Exception e) { SRApi.LogWarn("[FeatureRuntime] 撤销 " + id + " 失败: " + e.Message); }
            // 账本内登记的撤销动作逆序执行；单项失败不影响其余项。
            for (int i = lease.Revokes.Count - 1; i >= 0; i--) {
                try { lease.Revokes[i](); }
                catch (Exception e) { SRApi.LogWarn("[FeatureRuntime] " + id + " 撤销项失败: " + e.Message); }
            }
            lease.Revokes.Clear(); lease.Subscriptions.Clear();
            _leases.Remove(id);
            _order.Remove(id);
            foreach (Type x in new List<Type>(_types)) if (x.FullName == lease.TypeName) _types.Remove(x);
            return true;
        }
        public static int DisposeAll() {
            string[] ids = _order.ToArray();
            int n = 0;
            for (int i = ids.Length - 1; i >= 0; i--) if (Dispose(ids[i])) n++;
            _stack.Clear();
            return n;
        }

        public static string Report() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("功能作用域 ").Append(_leases.Count).Append(" 个 / 幂等类型 ").Append(_types.Count).AppendLine();
            sb.Append("未声明门控 ").Append(UndeclaredGates()).Append(" 个 / 反射缓存 ").Append(RefCache.DeclaredCount).AppendLine();
            sb.Append("委托撤销 ").Append(TotalRevokes()).Append(" 项 / 订阅 ").Append(TotalSubscriptions()).Append(" 项").AppendLine();
            sb.Append("UI 层冲突 ").Append(UiLayerRegistry.Conflicts.Count).Append(" 条 / 消息号冲突 ").Append(NetMsgId.Conflicts.Count).AppendLine();
            if (RefCache.Misses.Count > 0) sb.Append("反射解析失败 ").Append(RefCache.Misses.Count).Append(" 项").AppendLine();
            int ticks = 0, failed = 0;
            foreach (FeatureLease l in _leases.Values) {
                ticks += l.Ticks.Count;
                if (!l.InitOk) failed++;
            }
            if (failed > 0) sb.Append("初始化失败 ").Append(failed).AppendLine(" 个");
            // 逐个列出作用域 id：排障时要能直接看出"哪些功能占着资源"，只给总数没用。
            foreach (string id in _order) {
                if (!_leases.ContainsKey(id)) continue;
                FeatureLease l = _leases[id];
                sb.Append("  · ").Append(id).Append("（").Append(l.Total).Append(" 项）").AppendLine();
            }
            sb.Append("登记 tick=").Append(ticks).AppendLine();
            return sb.ToString();
        }
    }


    // API 门面：功能层只允许用这里的成员；日志出口由核心在启动时注入。
}
