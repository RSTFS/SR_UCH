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
