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
public partial class ModManager {

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
            try { HarmonyLib.Harmony.UnpatchID(guid); } catch { }
            try {
                HarmonyLib.Harmony h = new HarmonyLib.Harmony("SR_UCH.Unpatch");
                foreach (MethodBase mb in HarmonyLib.Harmony.GetAllPatchedMethods()) {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(mb);
                    if (pi == null) continue;
                    foreach (var p in pi.Prefixes) if (p.owner == guid) { try { h.Unpatch(mb, p.PatchMethod); } catch { } }
                    foreach (var p in pi.Postfixes) if (p.owner == guid) { try { h.Unpatch(mb, p.PatchMethod); } catch { } }
                    foreach (var p in pi.Transpilers) if (p.owner == guid) { try { h.Unpatch(mb, p.PatchMethod); } catch { } }
                    foreach (var p in pi.Finalizers) if (p.owner == guid) { try { h.Unpatch(mb, p.PatchMethod); } catch { } }
                }
            } catch { }
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
                } catch { }
            }
        }

        private static void SaveDisabledPlugins() {
            List<string> list = new List<string>();
            foreach (var kv in _extDisabled) {
                if (kv.Value) list.Add(kv.Key);
            }
            _disabledPluginsEntry.Value = string.Join(";", list.ToArray());
            _disabledPluginsEntry.ConfigFile.Save();
        }

        //applied once after the plugin scan:
        //1) 重新禁用上次会话禁用的插件（持久化列表）
        //2) 默认禁用所有外部 mod（SR_UCH 已整合大多数增强功能，避免重复/冲突）；
        //   用户手动启用后 GUID 会从列表移除，重启后保持启用。
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
            //默认禁用所有外部 mod（不在持久化启用列表中的）
            foreach (PluginEntry p in _externalPlugins) {
                if (disabledSet.Contains(p.guid)) {
                    _extDisabled[p.guid] = true;
                    DisablePlugin(p);
                } else {
                    //未在列表 = 默认禁用（首次），写入列表持久化
                    disabledSet.Add(p.guid);
                    _extDisabled[p.guid] = true;
                    DisablePlugin(p);
                    changed = true;
                }
            }
            if (changed) {
                _disabledPluginsEntry.Value = string.Join(";", disabledSet);
                try { _disabledPluginsEntry.ConfigFile.Save(); } catch { }
            }
        }

        private static void ToggleExternalPlugin(PluginEntry p) {
            bool disabled = !IsExternalDisabled(p.guid);
            _extDisabled[p.guid] = disabled;
            if (disabled) DisablePlugin(p);
            else EnablePlugin(p);
            SaveDisabledPlugins();
        }


        private static PluginEntry CurrentExternalPlugin() {
            foreach (PluginEntry p in _externalPlugins) if (p.guid == _pluginKey) return p;
            return _externalPlugins.Count > 0 ? _externalPlugins[0] : null;
        }

	}
}
