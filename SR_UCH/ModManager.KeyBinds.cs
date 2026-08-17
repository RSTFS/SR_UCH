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

// ==== 分区：KeyBinds（自定义键位 / Shift·Ctrl·Alt 组合键 / 捕捉与显示）====

        //features register their hotkeys here; the manager shows "Shift + X" for combo keys.
        //组合键持久化：每个键位自动建一个隐藏配置项（设置\组合键 <Key>）存修饰键，
        //这样捕捉时设置过的组合在重启后仍保留；未设置过的条目默认无修饰。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) {
            _shiftKeys.Remove(entry);
            RegisterComboEntry(entry);
        }

        public static void RegisterShiftKey(string name, ConfigEntry<KeyCode> entry, string mode) {
            _shiftKeys.Add(entry);
            RegisterComboEntry(entry);
        }

        //为键位建立持久化修饰键配置（惰性：捕捉时首次设置才真正写入）
        private static void RegisterComboEntry(ConfigEntry<KeyCode> entry) {
            if (entry == null || _keyModEntries.ContainsKey(entry)) return;
            try {
                if (_internalConfig != null) {
                    ConfigEntry<string> modEntry = _internalConfig.Bind("设置", "组合键 " + entry.Definition.Key, "",
                        "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                    _keyModEntries[entry] = modEntry;
                    string v = modEntry.Value;
                    ComboMod m = ParseComboMod(v);
                    if (m != ComboMod.None) _keyMods[entry] = m;
                    else _keyMods.Remove(entry);
                }
                //_internalConfig 未就绪：不建条目，留给 SetKeyComboMod 补建
            } catch { }
        }

        private static ComboMod ParseComboMod(string v) {
            if (v == "Shift") return ComboMod.Shift;
            if (v == "Ctrl") return ComboMod.Ctrl;
            if (v == "Alt") return ComboMod.Alt;
            return ComboMod.None;
        }

        //设置键位的组合修饰（运行时 + 持久化）
        private static void SetKeyComboMod(ConfigEntryBase entry, ComboMod mod) {
            if (entry == null) return;
            if (mod == ComboMod.None) _keyMods.Remove(entry);
            else _keyMods[entry] = mod;
            ConfigEntry<string> me;
            if (!_keyModEntries.TryGetValue(entry, out me) || me == null) {
                //未预注册（如 External 键位或 _internalConfig 尚未就绪）：此时补建持久化配置
                if (_internalConfig != null) {
                    try {
                        me = _internalConfig.Bind("设置", "组合键 " + entry.Definition.Key, "",
                            "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                        _keyModEntries[entry] = me;
                    } catch { me = null; }
                }
            }
            if (me != null) {
                try {
                    string v = mod == ComboMod.Shift ? "Shift" : mod == ComboMod.Ctrl ? "Ctrl" : mod == ComboMod.Alt ? "Alt" : "";
                    if (me.Value != v) { me.Value = v; _dirty = true; }
                } catch { }
            }
        }

        //功能初始化时为键位设置默认组合修饰（仅当用户从未设置过时生效）
        public static void SetDefaultCombo(ConfigEntryBase entry, ComboMod mod) {
            if (entry == null || _keyMods.ContainsKey(entry)) return;
            //持久化配置里已有值 → 尊重用户设置
            ConfigEntry<string> me;
            if (_keyModEntries.TryGetValue(entry, out me) && me != null) {
                try {
                    if (!string.IsNullOrEmpty(me.Value)) {
                        ComboMod saved = ParseComboMod(me.Value);
                        if (saved != ComboMod.None) { _keyMods[entry] = saved; return; }
                    }
                } catch { }
            }
            SetKeyComboMod(entry, mod);
        }

        //当前条目需要的修饰键（无则 None）
        public static ComboMod KeyComboMod(ConfigEntryBase entry) {
            ComboMod m;
            if (entry != null && _keyMods.TryGetValue(entry, out m)) return m;
            return ComboMod.None;
        }

        //组合键匹配：修饰键当前是否按下（None 恒 true）
        public static bool ComboModDown(ConfigEntryBase entry) {
            switch (KeyComboMod(entry)) {
                case ComboMod.Shift: return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                case ComboMod.Ctrl: return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case ComboMod.Alt: return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                default: return true;
            }
        }

        //按下检测（含修饰键）：ComboKeyDown(entry) == 修饰键按住 + 主键按下
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            return ComboModDown(entry) && Input.GetKeyDown(entry.Value);
        }

        //按住检测（含修饰键）
        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            return ComboModDown(entry) && Input.GetKey(entry.Value);
        }

        //松开检测（含修饰键）
        public static bool ComboKeyUp(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            return ComboModDown(entry) && Input.GetKeyUp(entry.Value);
        }

        //组合键显示文本："Shift + X" / "Ctrl + X" / "Alt + X" / 单键
        public static string ComboKeyDisplay(ConfigEntryBase entry, KeyCode kc) {
            if (kc == KeyCode.None) return KeyDisplayName(kc);
            switch (KeyComboMod(entry)) {
                case ComboMod.Shift: return "Shift + " + KeyDisplayName(kc);
                case ComboMod.Ctrl: return "Ctrl + " + KeyDisplayName(kc);
                case ComboMod.Alt: return "Alt + " + KeyDisplayName(kc);
                default: return KeyDisplayName(kc);
            }
        }

	}
}
