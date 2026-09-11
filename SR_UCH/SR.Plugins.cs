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

        //external plugins can be disabled/enabled at runtime; the state persists in the config
        private static readonly Dictionary<string, bool> _extDisabled = new Dictionary<string, bool>();

        private static bool IsExternalDisabled(string guid) {
            bool v;
            return _extDisabled.TryGetValue(guid, out v) && v;
        }

        //unpatch every Harmony patch owned by this plugin (by id AND by scanning all patches)
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
            if (p.instance != null) {
                foreach (Behaviour b in p.instance.GetComponents<Behaviour>()) {
                    if (b != null) b.enabled = false;
                }
            }
            UnpatchPlugin(p.guid);
        }

        private static void EnablePlugin(PluginEntry p) {
            if (p.instance != null) {
                foreach (Behaviour b in p.instance.GetComponents<Behaviour>()) {
                    if (b != null) b.enabled = true;
                }
                try {
                    new HarmonyLib.Harmony(p.guid).PatchAll(p.instance.GetType().Assembly);
                } catch (Exception __ex) { Guard.Log("重新应用外部插件补丁: " + p.guid, __ex); }
            }
        }

        //外部插件默认启用（deny-list）：只有用户手动禁用的插件 GUID 记入列表并禁用。
        //不在列表内 = 默认启用（首次扫描全部保持启用）。
        private static void SaveEnabledPlugins() {
            List<string> list = new List<string>();
            foreach (var kv in _extDisabled) {
                if (kv.Value) list.Add(kv.Key);
            }
            _disabledPluginsEntry.Value = string.Join(";", list.ToArray());
            _disabledPluginsEntry.ConfigFile.Save();
        }

        //applied once after the plugin scan:
        //1) 默认启用所有外部 mod（不再自动禁用；SR_UCH 已整合的功能默认不冲突，
        //   因为外部插件各自独立加载，由 BepInEx 启动时初始化）
        //2) 用户手动禁用过的插件（持久化禁用列表内）保持禁用，重启后仍生效
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
            //默认启用所有外部 mod；禁用列表内的插件保持禁用
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

	}
}
