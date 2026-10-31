// 外部插件管理页。扫描 plugins 目录下的插件和语言包 DLL，负责禁用 / 启用 / 记住状态。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Plugins（外部插件管理：扫描 / 禁用 / 启用 / 持久化）

        private class PluginEntry {
            public string guid;
            public string name;
            public ConfigFile config;
            public BaseUnityPlugin instance;
            public string path;   // 程序集路径：外部 DLL 来源必须可见
        }

        //外部插件的禁用状态（持久化在配置项 DisabledPlugins 中）。
        private static readonly Dictionary<string, bool> _extDisabled = new Dictionary<string, bool>();


        private static bool IsExternalDisabled(string guid) {
            bool v;
            return _extDisabled.TryGetValue(guid, out v) && v;
        }

        //设置页不渲染原始的禁用 GUID 字符串条目（改由顶部按钮清单呈现）。
        private static bool SettingsHiddenDisabledPlugins(ConfigEntryBase e) {
            return !(e.Definition.Section == Keys.Settings && e.Definition.Key == Keys.DisabledPlugins);
        }

        //窗口宽高 / XY 配置项仅用于持久化，不在设置页显示（窗口可直接拖动缩放与移动）。
        private static bool SettingsHiddenWindowGeometry(ConfigEntryBase e) {
            if (e.Definition.Section != Keys.Settings) return true;
            string k = e.Definition.Key;
            return !(k == Keys.WinWidth || k == Keys.WinHeight || k == Keys.WinX || k == Keys.WinY);
        }

        private static PluginEntry EntryByGuid(string guid) {
            for (int i = 0; i < _externalPlugins.Count; i++) {
                if (_externalPlugins[i].guid == guid) return _externalPlugins[i];
            }
            return null;
        }

        //该 GUID 对应插件的程序集，供按程序集认领补丁的兜底解绑；取不到返回 null。
        private static Assembly AssemblyOf(string guid) {
            try {
                PluginEntry p = EntryByGuid(guid);
                if (p != null && p.instance != null) return p.instance.GetType().Assembly;
            } catch (Exception __ex) { Guard.Log("定位插件程序集", __ex); }
            return null;
        }

        //判断补丁是否属于该插件：owner 等于 GUID，或 PatchMethod 所在程序集等于该插件程序集。
        //不认领 SR_UCH 自身程序集，避免误删本 mod 的补丁。
        private static bool PatchOwnedBy(HarmonyLib.Patch patch, string guid, Assembly target, Assembly mine) {
            try {
                if (patch == null) return false;
                if (patch.owner == guid) return true;
                if (target == null) return false;
                MethodBase pm = patch.PatchMethod;
                Type dt = pm != null ? pm.DeclaringType : null;
                if (dt == null) return false;
                Assembly a = dt.Assembly;
                if (a == null || a == mine) return false;
                return a == target;
            } catch { return false; }
        }

        //撤销该插件的全部 Harmony 补丁：先按 ID，再扫描补丁表按 owner / 程序集兜底解绑。
        //解绑共用一份静态 Harmony 实例。
        private static HarmonyLib.Harmony _unpatcher;
        private static void UnpatchPlugin(string guid) {
            Guard.Try("撤销外部插件补丁(UnpatchID): " + guid, () => HarmonyLib.Harmony.UnpatchID(guid));
            Assembly target = AssemblyOf(guid);
            Assembly mine = typeof(SR).Assembly;
            try {
                if (_unpatcher == null) _unpatcher = new HarmonyLib.Harmony("SR_UCH.Unpatch");
                HarmonyLib.Harmony h = _unpatcher;
                foreach (MethodBase mb in HarmonyLib.Harmony.GetAllPatchedMethods()) {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(mb);
                    if (pi == null) continue;
                    foreach (var p in pi.Prefixes) if (PatchOwnedBy(p, guid, target, mine)) { Guard.Try("解绑 Prefix", () => h.Unpatch(mb, p.PatchMethod)); }
                    foreach (var p in pi.Postfixes) if (PatchOwnedBy(p, guid, target, mine)) { Guard.Try("解绑 Postfix", () => h.Unpatch(mb, p.PatchMethod)); }
                    foreach (var p in pi.Transpilers) if (PatchOwnedBy(p, guid, target, mine)) { Guard.Try("解绑 Transpiler", () => h.Unpatch(mb, p.PatchMethod)); }
                    foreach (var p in pi.Finalizers) if (PatchOwnedBy(p, guid, target, mine)) { Guard.Try("解绑 Finalizer", () => h.Unpatch(mb, p.PatchMethod)); }
                }
            } catch (Exception __ex) { Guard.Log("扫描补丁表并解绑", __ex); }
        }

        private static void DisablePlugin(PluginEntry p) {
            //所有插件同挂在一个 BepInEx_Manager 物体上，只能关本插件自身组件，不可遍历该物体的全部组件。
            if (p.instance != null) p.instance.enabled = false;
            UnpatchPlugin(p.guid);
        }

        private static void EnablePlugin(PluginEntry p) {
            if (p.instance != null) {
                p.instance.enabled = true; //只开本插件自身组件
                //先解绑再 PatchAll（解绑幂等），否则会重复打上未被清掉的补丁。
                UnpatchPlugin(p.guid);
                try {
                    new HarmonyLib.Harmony(p.guid).PatchAll(p.instance.GetType().Assembly);
                } catch (Exception __ex) { Guard.Log("重新应用外部插件补丁: " + p.guid, __ex); }
            }
        }

        //外部插件默认启用：配置项只记录用户手动禁用的 GUID。
        private static void SaveEnabledPlugins() {
            List<string> list = new List<string>();
            foreach (var kv in _extDisabled) {
                if (kv.Value) list.Add(kv.Key);
            }
            _disabledPluginsEntry.Value = string.Join(";", list.ToArray());
            MarkConfigDirty(_disabledPluginsEntry.ConfigFile); //交给 SR.FlushConfig 节流落盘
        }

        //插件扫描后执行一次：启用全部外部 mod，仅保持持久化禁用列表中记录的插件为禁用。
        private static void ApplyDisabledPlugins() {
            if (_appliedDisabled) return;
            _appliedDisabled = true;
            string s = _disabledPluginsEntry.Value;
            HashSet<string> disabledSet = new HashSet<string>();
            if (!string.IsNullOrEmpty(s)) {
                foreach (string g in s.Split(';')) {
                    if (!string.IsNullOrEmpty(g)) disabledSet.Add(g);
                }
            }
            bool changed = false;
            //清理已卸载插件的 GUID
            foreach (string g in new List<string>(disabledSet)) {
                if (!Chainloader.PluginInfos.ContainsKey(g)) { disabledSet.Remove(g); changed = true; }
            }
            foreach (PluginEntry p in _externalPlugins) {
                if (disabledSet.Contains(p.guid)) {
                    _extDisabled[p.guid] = true;
                    DisablePlugin(p);
                } else {
                    _extDisabled[p.guid] = false;
                }
            }
            if (changed) {
                _disabledPluginsEntry.Value = string.Join(";", disabledSet);
                MarkConfigDirty(_disabledPluginsEntry.ConfigFile);
            }
        }

        private static void ToggleExternalPlugin(PluginEntry p) {
            bool disabled = !IsExternalDisabled(p.guid);
            _extDisabled[p.guid] = disabled;
            if (disabled) DisablePlugin(p);
            else EnablePlugin(p);
            SaveEnabledPlugins(); //立即落盘
        }

        private static PluginEntry CurrentExternalPlugin() {
            foreach (PluginEntry p in _externalPlugins) if (p.guid == _pluginKey) return p;
            return _externalPlugins.Count > 0 ? _externalPlugins[0] : null;
        }

        //设置页顶部的插件清单：每行 = 启用/禁用按钮 + 插件名（悬停显示 GUID）。
        // 外部插件程序集路径（来源可见性：设置页悬停显示）。
        private static string SafeAssemblyPath(BaseUnityPlugin p) {
            try { return p != null && p.GetType().Assembly != null ? p.GetType().Assembly.Location : ""; }
            catch { return ""; }
        }

        private static void RenderPluginList() {
            try {
                if (_externalPlugins.Count == 0) return;
                GUILayout.Label(T("- 外部插件（运行时启用/禁用）-", "- External plugins (enable/disable at runtime) -"), _secHeader);
                //按可用宽度自动换行
                WrapLabel(T("禁用会立刻卸载该插件的 Harmony 补丁并停掉它，重启后保持；启用会重新应用补丁。",
                            "Disabling unpatches and stops the plugin immediately and persists after a restart; enabling re-applies its patches."));
                GUILayout.Space(Sc(2));
                float w = Mathf.Max(Sc(160), _winWidth - SidebarWidth() - Sc(40));
                foreach (PluginEntry p in _externalPlugins) {
                    bool dis = IsExternalDisabled(p.guid);
                    GUILayout.BeginHorizontal(GUILayout.Width(w));
                    if (GUILayout.Button(dis ? T("启用", "Enable") : T("禁用", "Disable"),
                            dis ? _checkOn : _btn, GUILayout.Width(Sc(58)), GUILayout.Height(Sc(24)))) {
                        ToggleExternalPlugin(p);
                    }
                    GUILayout.Space(Sc(6));
                    Color prev = GUI.color;
                    try {
                    if (dis) GUI.color = new Color(1f, 1f, 1f, 0.55f);   //禁用项用半透明标记
                    GUILayout.Label(new GUIContent((dis ? "* " : "") + p.name,
                        p.guid + "\n" + (string.IsNullOrEmpty(p.path) ? "?" : p.path) + "\n" + (dis ? T("已禁用", "disabled") : T("已启用", "enabled"))),
                        _labelClip, GUILayout.Width(w - Sc(70)), GUILayout.Height(Sc(24)));
                    } finally { GUI.color = prev; }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(Sc(6));
            } catch (Exception __ex) { Guard.Log("外部插件清单渲染", __ex); }
        }


        // ============================================================
        // 分区：补丁归因（谁改了同一个游戏方法）
        // 目的：自检只能证明"我们的目标存在"，不能证明"别人也在改同一个方法"。
        // 这里用 Harmony 自带的全局补丁表，把每个被改方法的补丁按"声明程序集"分组，
        // 分出"我们 / 其它 mod"，并把其它 mod 还原成 GUID+版本（经 Chainloader 查表）。
        // 只读查询，不改任何补丁；扫描推迟到启动 3 秒后（等其它插件打完补丁）。
        // ============================================================

        private sealed class SharedMethodInfo {
            public string Signature;
            public List<string> Owners = new List<string>();
        }

        private static readonly List<SharedMethodInfo> _sharedMethods = new List<SharedMethodInfo>();
        private static readonly List<string> _foreignPatchers = new List<string>();
        private static readonly Dictionary<Assembly, string> _modLabels = new Dictionary<Assembly, string>();
        private static int _attrPatched;
        private static int _attrOurs;
        private static int _attrShared;
        private static bool _attrDone;
        private static bool _attrArmed;
        private static float _attrAt;
        internal static string AttributionError = "";

        internal static bool AttributionReady { get { return _attrDone; } }

        internal static void InitAttribution() {
            try {
                _attrArmed = true;
                _attrAt = Time.unscaledTime + 3f;
                RegisterTick("补丁归因", TickAttribution);
            } catch (Exception __ex) { Guard.Log("补丁归因 初始化", __ex); }
        }

        private static void TickAttribution() {
            try {
                if (!_attrArmed || Time.unscaledTime < _attrAt) return;
                _attrArmed = false;
                ScanPatches();
            } catch (Exception __ex) { Guard.Log("补丁归因 Tick", __ex); }
        }

        // 把程序集还原成"插件名 版本"；查不到就退回程序集名。结果缓存（同一程序集只算一次）。
        private static string ModLabel(Assembly asm) {
            if (asm == null) return "?";
            string hit;
            if (_modLabels.TryGetValue(asm, out hit)) return hit;
            string label = null;
            try {
                foreach (KeyValuePair<string, PluginInfo> kv in Chainloader.PluginInfos) {
                    PluginInfo pi = kv.Value;
                    if (pi == null || pi.Instance == null) continue;
                    Assembly pa = null;
                    try { pa = pi.Instance.GetType().Assembly; } catch (Exception __pa) { Guard.Log("补丁归因/取插件程序集", __pa); }
                    if (pa != asm) continue;
                    string name = pi.Metadata != null ? pi.Metadata.Name : kv.Key;
                    string ver = pi.Metadata != null ? pi.Metadata.Version.ToString() : "";
                    label = string.IsNullOrEmpty(ver) ? name : (name + " " + ver);
                    break;
                }
            } catch (Exception __ex) { Guard.Log("补丁归因/查插件表", __ex); }
            if (string.IsNullOrEmpty(label)) {
                try { label = asm.GetName().Name; } catch { label = "?"; }
            }
            _modLabels[asm] = label;
            return label;
        }

        private static string MethodSignature(MethodBase m) {
            try {
                string type = m.DeclaringType != null ? m.DeclaringType.Name : "?";
                return type + "." + m.Name;
            } catch { return "?"; }
        }

        private static void CollectPatches(Patches info, List<Patch> into) {
            if (info == null) return;
            if (info.Prefixes != null) into.AddRange(info.Prefixes);
            if (info.Postfixes != null) into.AddRange(info.Postfixes);
            if (info.Transpilers != null) into.AddRange(info.Transpilers);
            if (info.Finalizers != null) into.AddRange(info.Finalizers);
        }

        internal static void ScanPatches() {
            try {
                AttributionError = "";
                _sharedMethods.Clear();
                _foreignPatchers.Clear();
                _modLabels.Clear();
                _attrPatched = 0;
                _attrOurs = 0;
                _attrShared = 0;

                Assembly ours = typeof(SR).Assembly;
                System.Collections.IEnumerable all = null;
                try {
                    MethodInfo gam = typeof(Harmony).GetMethod("GetAllPatchedMethods",
                        BindingFlags.Public | BindingFlags.Static);
                    if (gam != null) all = gam.Invoke(null, null) as System.Collections.IEnumerable;
                } catch (Exception __gm) { Guard.Log("补丁归因/取全局补丁表", __gm); }
                if (all == null) { AttributionError = "当前 Harmony 不支持 GetAllPatchedMethods()"; _attrDone = true; return; }

                HashSet<string> foreignSet = new HashSet<string>();
                List<Patch> buf = new List<Patch>(8);
                foreach (object o in all) {
                    MethodBase m = o as MethodBase;
                    if (m == null) continue;
                    _attrPatched++;
                    Patches info = null;
                    try { info = Harmony.GetPatchInfo(m); } catch (Exception __pi) { Guard.Log("补丁归因/取补丁信息 " + MethodSignature(m), __pi); continue; }
                    if (info == null) continue;

                    buf.Clear();
                    CollectPatches(info, buf);

                    bool mine = false;
                    List<string> foreign = null;
                    for (int i = 0; i < buf.Count; i++) {
                        Patch p = buf[i];
                        Assembly asm = null;
                        try { asm = p.PatchMethod != null && p.PatchMethod.DeclaringType != null ? p.PatchMethod.DeclaringType.Assembly : null; }
                        catch (Exception __asm) { Guard.Log("补丁归因/取声明程序集", __asm); }
                        if (asm == ours) { mine = true; continue; }
                        string label = ModLabel(asm);
                        if (foreign == null) foreign = new List<string>();
                        if (!foreign.Contains(label)) foreign.Add(label);
                        if (foreignSet.Count < 64) foreignSet.Add(label);
                    }
                    if (mine) _attrOurs++;
                    if (foreign != null && foreign.Count > 0) {
                        _attrShared++;
                        if (_sharedMethods.Count < 40)
                            _sharedMethods.Add(new SharedMethodInfo { Signature = MethodSignature(m), Owners = foreign });
                    }
                }
                foreach (string s in foreignSet) _foreignPatchers.Add(s);
                _foreignPatchers.Sort(StringComparer.OrdinalIgnoreCase);
                _sharedMethods.Sort(delegate (SharedMethodInfo a, SharedMethodInfo b) { return string.CompareOrdinal(a.Signature, b.Signature); });
                _attrDone = true;
                LogInfo("[补丁归因] 扫描 " + _attrPatched + " 个被改方法：我们参与 " + _attrOurs + "，其它 mod 参与 " + _attrShared
                    + ( _foreignPatchers.Count > 0 ? "；其它 mod：" + string.Join(", ", _foreignPatchers.ToArray()) : ""));
            } catch (Exception __ex) {
                AttributionError = __ex.Message;
                Guard.Log("补丁归因/扫描", __ex);
            }
        }

        // 供诊断页与报告使用：多行文本。
        internal static string AttributionReport() {
            try {
                if (!_attrDone) return "";
                if (!string.IsNullOrEmpty(AttributionError)) return "补丁归因不可用：" + AttributionError;
                StringBuilder sb = new StringBuilder();
                sb.Append("补丁归因：扫描 ").Append(_attrPatched).Append(" 个被改方法，本模组参与 ").Append(_attrOurs)
                  .Append(" 个，另有其它 mod 参与 ").Append(_attrShared).AppendLine(" 个");
                if (_foreignPatchers.Count > 0) {
                    sb.Append("其它改动游戏方法的 mod：").AppendLine(string.Join(", ", _foreignPatchers.ToArray()));
                }
                for (int i = 0; i < _sharedMethods.Count; i++) {
                    SharedMethodInfo s = _sharedMethods[i];
                    sb.Append("  ").Append(s.Signature).Append("  <- ").AppendLine(string.Join(", ", s.Owners.ToArray()));
                }
                if (_sharedMethods.Count == 0 && _foreignPatchers.Count == 0) {
                    sb.AppendLine("目前只有本模组在打补丁。");
                }
                return sb.ToString();
            } catch (Exception __ex) { Guard.Log("补丁归因/报告", __ex); return ""; }
        }
    }
}
