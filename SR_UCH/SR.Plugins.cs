using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Plugins（外部插件管理：扫描 / 禁用 / 启用 / 持久化）====

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

        //「设置」页里不渲染原始的禁用 GUID 字符串条目（改为顶部按钮清单）
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

        //撤销该插件的全部 Harmony 补丁（先按 ID，再扫描补丁表兜底）
        private static void UnpatchPlugin(string guid) {
            Guard.Try("撤销外部插件补丁(UnpatchID): " + guid, () => HarmonyLib.Harmony.UnpatchID(guid));
            try {
                HarmonyLib.Harmony h = new HarmonyLib.Harmony("SR_UCH.Unpatch");
                foreach (MethodBase mb in HarmonyLib.Harmony.GetAllPatchedMethods()) {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(mb);
                    if (pi == null) continue;
                    foreach (var p in pi.Prefixes) if (p.owner == guid) { Guard.Try("解绑 Prefix", () => h.Unpatch(mb, p.PatchMethod)); }
                    foreach (var p in pi.Postfixes) if (p.owner == guid) { Guard.Try("解绑 Postfix", () => h.Unpatch(mb, p.PatchMethod)); }
                    foreach (var p in pi.Transpilers) if (p.owner == guid) { Guard.Try("解绑 Transpiler", () => h.Unpatch(mb, p.PatchMethod)); }
                    foreach (var p in pi.Finalizers) if (p.owner == guid) { Guard.Try("解绑 Finalizer", () => h.Unpatch(mb, p.PatchMethod)); }
                }
            } catch (Exception __ex) { Guard.Log("扫描补丁表并解绑", __ex); }
        }

        private static void DisablePlugin(PluginEntry p) {
            //⚠ BepInEx 把**所有**插件都 AddComponent 到同一个 "BepInEx_Manager" GameObject 上
            //（Chainloader.Start：new GameObject → AddComponent(每个插件类型)）。
            //所以这里绝不能遍历 GetComponents<Behaviour>() 全关：那会把同物体上的其它插件（包括
            //ConfigurationManager，按 HOME 打不开 GUI）一起关掉。只关这个插件自己这一个组件。
            if (p.instance != null) p.instance.enabled = false;
            UnpatchPlugin(p.guid);
        }

        private static void EnablePlugin(PluginEntry p) {
            if (p.instance != null) {
                p.instance.enabled = true; //同理：只开它自己（全开会顺带把用户禁用的其它插件也打开）
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
            _disabledPluginsEntry.ConfigFile.Save();
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
                Guard.Try("外部插件禁用列表保存", () => _disabledPluginsEntry.ConfigFile.Save());
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

        //「设置」页顶部的插件清单：一行一个插件 = 启用/禁用按钮 + 名字（悬停显示 GUID）。
        //原来这里只有一个 "Disabled Plugins" 原始字符串条目（分号分隔的 GUID），既不好看也不好点。
        private static void RenderPluginList() {
            try {
                if (_externalPlugins.Count == 0) return;
                GUILayout.Label(T("— 外部插件（运行时启用/禁用）—", "— External plugins (enable/disable at runtime) —"), _secHeader);
                GUILayout.Label(T("禁用会立刻卸载该插件的 Harmony 补丁并停掉它，重启后保持；启用会重新应用补丁。",
                                  "Disabling unpatches and stops the plugin immediately and persists after a restart; enabling re-applies its patches."),
                                _label);
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
                    if (dis) GUI.color = new Color(1f, 1f, 1f, 0.55f);
                    GUILayout.Label(new GUIContent((dis ? "⛔ " : "") + p.name, p.guid + "\n" + (dis ? T("已禁用", "disabled") : T("已启用", "enabled"))),
                        _labelClip, GUILayout.Width(w - Sc(70)), GUILayout.Height(Sc(24)));
                    GUI.color = prev;
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(Sc(6));
            } catch (Exception __ex) { Guard.Log("外部插件清单渲染", __ex); }
        }

	}
}
