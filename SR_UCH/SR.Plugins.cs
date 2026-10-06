// 外部插件管理页。扫描 plugins 目录下的插件和语言包 DLL，负责禁用 / 启用 / 记住状态。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
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
        }

        //外部插件可运行时启用/禁用，状态持久化在配置里
        private static readonly Dictionary<string, bool> _extDisabled = new Dictionary<string, bool>();


        private static bool IsExternalDisabled(string guid) {
            bool v;
            return _extDisabled.TryGetValue(guid, out v) && v;
        }

        //设置页里不渲染原始的禁用 GUID 字符串条目（改为顶部按钮清单）
        private static bool SettingsHiddenDisabledPlugins(ConfigEntryBase e) {
            return !(e.Definition.Section == "Settings" && e.Definition.Key == "Disabled Plugins");
        }

        //窗口宽高/XY 不再做成可自定义条目（窗口直接拖右下角缩放、拖标题栏移动即可）：
        //配置项本身保留（用于持久化），只是不在设置页显示
        private static bool SettingsHiddenWindowGeometry(ConfigEntryBase e) {
            if (e.Definition.Section != "Settings") return true;
            string k = e.Definition.Key;
            return !(k == "Window Width" || k == "Window Height" || k == "Window X" || k == "Window Y");
        }

        private static PluginEntry EntryByGuid(string guid) {
            for (int i = 0; i < _externalPlugins.Count; i++) {
                if (_externalPlugins[i].guid == guid) return _externalPlugins[i];
            }
            return null;
        }

        //该 GUID 对应插件的程序集（用于"按程序集认领补丁"的兜底解绑；取不到 = null，只按 owner 比对）
        private static Assembly AssemblyOf(string guid) {
            try {
                PluginEntry p = EntryByGuid(guid);
                if (p != null && p.instance != null) return p.instance.GetType().Assembly;
            } catch (Exception __ex) { Guard.Log("定位插件程序集", __ex); }
            return null;
        }

        //这个补丁是不是属于该插件？两条判据：
        //  1) owner == GUID（BepInEx 惯例：插件用 new Harmony(GUID) 打补丁）；
        //  2) PatchMethod 所在程序集 == 该插件程序集, 覆盖"插件用了别的 Harmony id"和
        //     "插件在 Awake 里自己 PatchAll"这两种按 owner 认不出来的情况（否则禁用时清不干净，
        //     再次启用就会把同一 patch 再打一份 -> 双重执行）。
        //绝不认领 SR_UCH 自己的程序集，避免误删本 mod 的补丁。
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

        //撤销该插件的全部 Harmony 补丁（先按 ID，再扫描补丁表兜底：按 owner 或按程序集认领）
        // **P2-04**：解绑用的 Harmony 实例复用一份静态的。原来每次 UnpatchPlugin 都 new 一个，
        //  频繁启用/禁用插件时会在 Harmony 内部不断堆积同一个 id 的补丁器状态。
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
            // BepInEx 把**所有**插件都 AddComponent 到同一个 "BepInEx_Manager" GameObject 上
            //（Chainloader.Start：new GameObject -> AddComponent(每个插件类型)）。
            //所以这里绝不能遍历 GetComponents<Behaviour>() 全关：那会把同物体上的其它插件（包括
            //ConfigurationManager，按 HOME 打不开 GUI）一起关掉。只关这个插件自己这一个组件。
            if (p.instance != null) p.instance.enabled = false;
            UnpatchPlugin(p.guid);
        }

        private static void EnablePlugin(PluginEntry p) {
            if (p.instance != null) {
                p.instance.enabled = true; //同理：只开它自己（全开会顺带把用户禁用的其它插件也打开）
                //启用前先解绑干净（解绑是幂等的）：以前只依赖"禁用时解绑成功"，插件若用了别的 Harmony id
                //或在 Awake 里自打补丁，禁用时清不掉 -> 这里不经解绑直接 PatchAll 就会把同一 patch 打第二份（双重执行）。
                UnpatchPlugin(p.guid);
                try {
                    new HarmonyLib.Harmony(p.guid).PatchAll(p.instance.GetType().Assembly);
                } catch (Exception __ex) { Guard.Log("重新应用外部插件补丁: " + p.guid, __ex); }
            }
        }

        //外部插件默认启用（deny-list）：只有用户手动禁用的插件 GUID 会记入列表并禁用。
        private static void SaveEnabledPlugins() {
            List<string> list = new List<string>();
            foreach (var kv in _extDisabled) {
                if (kv.Value) list.Add(kv.Key);
            }
            _disabledPluginsEntry.Value = string.Join(";", list.ToArray());
            MarkConfigDirty(_disabledPluginsEntry.ConfigFile); //交给 SR.FlushConfig 节流落盘，不在点击当帧写磁盘
        }

        //插件扫描后执行一次：默认启用所有外部 mod（各自由 BepInEx 独立加载、独立初始化）；
        //用户手动禁用过的（持久化禁用列表内）保持禁用，重启后仍生效。
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
                MarkConfigDirty(_disabledPluginsEntry.ConfigFile); //清理失效 GUID 的结果同样交给节流落盘
            }
        }

        private static void ToggleExternalPlugin(PluginEntry p) {
            bool disabled = !IsExternalDisabled(p.guid);
            _extDisabled[p.guid] = disabled;
            if (disabled) DisablePlugin(p);
            else EnablePlugin(p);
            SaveEnabledPlugins(); //启用/禁用都立即落盘，重启后保持本次状态
        }

        private static PluginEntry CurrentExternalPlugin() {
            foreach (PluginEntry p in _externalPlugins) if (p.guid == _pluginKey) return p;
            return _externalPlugins.Count > 0 ? _externalPlugins[0] : null;
        }

        //设置页顶部的插件清单：一行一个插件 = 启用/禁用按钮 + 名字（悬停显示 GUID）。
        //原来这里只有一个 "Disabled Plugins" 原始字符串条目（分号分隔的 GUID），既不好看也不好点。
        private static void RenderPluginList() {
            try {
                if (_externalPlugins.Count == 0) return;
                GUILayout.Label(T("- 外部插件（运行时启用/禁用）-", "- External plugins (enable/disable at runtime) -"), _secHeader);
                //按可用宽度自动换行（原来用 _label 不换行，窄窗口会被截断）
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
                    if (dis) GUI.color = new Color(1f, 1f, 1f, 0.55f);   // **R412**：禁用标记 [x] -> *
                    GUILayout.Label(new GUIContent((dis ? "* " : "") + p.name, p.guid + "\n" + (dis ? T("已禁用", "disabled") : T("已启用", "enabled"))),
                        _labelClip, GUILayout.Width(w - Sc(70)), GUILayout.Height(Sc(24)));
                    } finally { GUI.color = prev; }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(Sc(6));
            } catch (Exception __ex) { Guard.Log("外部插件清单渲染", __ex); }
        }

	}
}
