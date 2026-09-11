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

// ==== 分区：KeyBinds（自定义键位 / Shift·Ctrl·Alt 组合键 / 捕捉与显示）====

        //features register their hotkeys here; the manager shows "Shift + X" for combo keys.
        //组合键持久化：每个键位自动建一个隐藏配置项（设置\组合键 <Key>）存修饰键，
        //这样捕捉时设置过的组合在重启后仍保留；未设置过的条目默认无修饰。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) {
            RegisterComboEntry(entry);
        }

        public static void RegisterShiftKey(string name, ConfigEntry<KeyCode> entry, string mode) {
            RegisterComboEntry(entry);
        }

        //组合键持久化条目的**唯一名**：必须带分区名。
        //只用 Key 名会撞车：SR 与 EX 都有 "Respawn Key"（SR=重生点传送 / EX=复活），
        //共用同一个"组合键 Respawn Key"条目 → 一边绑定/清空会覆盖另一边，重启后组合键看起来"无效"。
        private static string ComboEntryName(ConfigEntryBase entry) {
            return entry.Definition.Section + " " + entry.Definition.Key;
        }

        //为键位建立持久化修饰键配置（惰性：捕捉时首次设置才真正写入）
        private static void RegisterComboEntry(ConfigEntry<KeyCode> entry) {
            if (entry == null || _keyModEntries.ContainsKey(entry)) return;
            try {
                if (_internalConfig != null) {
                    ConfigEntry<string> modEntry = _internalConfig.Bind("设置", "组合键 " + ComboEntryName(entry), "",
                        "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                    //兼容旧命名（只有 Key 名）：新条目还是空的时候把旧值搬过来，避免升级后组合键丢一次
                    try {
                        if (string.IsNullOrEmpty(modEntry.Value)) {
                            ConfigEntry<string> legacy = _internalConfig.Bind("设置", "组合键 " + entry.Definition.Key, "",
                                "组合键修饰（旧命名，已弃用；值会自动迁移到带分区名的新条目）");
                            if (!string.IsNullOrEmpty(legacy.Value)) {
                                modEntry.Value = legacy.Value;
                                legacy.Value = "";
                            }
                        }
                    } catch { }
                    _keyModEntries[entry] = modEntry;
                    _watchDirty = true;
                    string v = modEntry.Value;
                    ComboMod m = ParseComboMod(v);
                    if (m != ComboMod.None) _keyMods[entry] = m;
                    else _keyMods.Remove(entry);
                }
                //_internalConfig 未就绪：不建条目，留给 SetKeyComboMod 补建
            } catch (Exception __ex) { Guard.Log("建立按键组合条目", __ex); }
        }

        //解析持久化字符串：支持多修饰键 "Shift+Ctrl+Alt"（也兼容旧的单值 "Shift"/"Ctrl"/"Alt"）
        private static ComboMod ParseComboMod(string v) {
            ComboMod m = ComboMod.None;
            if (string.IsNullOrEmpty(v)) return m;
            foreach (string part in v.Split(new[] { '+', '|', ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                string p = part.Trim();
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) m |= ComboMod.Shift;
                else if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) m |= ComboMod.Ctrl;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) m |= ComboMod.Alt;
            }
            return m;
        }

        //格式化为持久化/显示字符串（固定顺序 Shift、Ctrl、Alt，空则返回 ""）
        private static string FormatComboMod(ComboMod m) {
            if (m == ComboMod.None) return "";
            string s = "";
            if ((m & ComboMod.Shift) != 0) s += "Shift";
            if ((m & ComboMod.Ctrl) != 0) s += (s.Length > 0 ? "+" : "") + "Ctrl";
            if ((m & ComboMod.Alt) != 0) s += (s.Length > 0 ? "+" : "") + "Alt";
            return s;
        }

        //当前物理按下的修饰键集合（捕捉与匹配都用它，支持多修饰键）
        public static ComboMod HeldComboMods() {
            ComboMod m = ComboMod.None;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= ComboMod.Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= ComboMod.Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= ComboMod.Alt;
            return m;
        }

        //=== 录制式快捷键：一串按键 + 顺序（如 Shift → 9 → 0）===
        //持久化格式仍然是一条字符串（与旧的"组合键"共用同一个隐藏条目）：
        //  "Shift+Ctrl+Alpha9" = 开头连续的修饰键（组合修饰）+ 之后的"序列前键"（Extras）；
        //  最后一个键始终是该键位自己的 ConfigEntry<KeyCode>（主键）。
        //所以三条能力是同一套模型：单键、修饰键组合、多键序列。

        private static bool IsModifierKey(KeyCode k) {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift ||
                   k == KeyCode.LeftControl || k == KeyCode.RightControl ||
                   k == KeyCode.LeftAlt || k == KeyCode.RightAlt;
        }

        //把持久化字符串拆成"开头连续修饰键的 ComboMod" + "其余序列前键"
        private static void ParseSeqParts(string v, out ComboMod mods, out List<KeyCode> extras) {
            mods = ComboMod.None;
            extras = new List<KeyCode>();
            if (string.IsNullOrEmpty(v)) return;
            bool modPhase = true;
            foreach (string part in v.Split(new[] { '+', '|', ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                string p = part.Trim();
                KeyCode k = ParseKeyName(p);
                if (k == KeyCode.None) continue;
                if (modPhase && IsModifierKey(k)) {
                    if (k == KeyCode.LeftShift || k == KeyCode.RightShift) mods |= ComboMod.Shift;
                    else if (k == KeyCode.LeftControl || k == KeyCode.RightControl) mods |= ComboMod.Ctrl;
                    else mods |= ComboMod.Alt;
                } else {
                    modPhase = false;
                    extras.Add(k);
                }
            }
        }

        //名称 → KeyCode：支持 Enum 名（"Alpha9"/"LeftShift"）与简写（"Shift"/"Ctrl"/"Alt"）
        private static KeyCode ParseKeyName(string p) {
            if (string.IsNullOrEmpty(p)) return KeyCode.None;
            if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftShift;
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftControl;
            if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftAlt;
            try {
                if (Enum.IsDefined(typeof(KeyCode), p)) return (KeyCode)Enum.Parse(typeof(KeyCode), p, true);
            } catch { }
            return KeyCode.None;
        }

        //序列前键（Extras）：从隐藏条目里解析出来（带缓存，键为"字符串+主键"）
        private static readonly Dictionary<ConfigEntryBase, KeyValuePair<string, List<KeyCode>>> _seqCache =
            new Dictionary<ConfigEntryBase, KeyValuePair<string, List<KeyCode>>>();

        private static List<KeyCode> SeqExtras(ConfigEntryBase entry) {
            List<KeyCode> extras;
            ComboMod mods;
            string raw = "";
            ConfigEntry<string> me;
            if (entry != null && _keyModEntries.TryGetValue(entry, out me) && me != null) { try { raw = me.Value ?? ""; } catch { raw = ""; } }
            KeyValuePair<string, List<KeyCode>> cached;
            if (entry != null && _seqCache.TryGetValue(entry, out cached) && cached.Key == raw) return cached.Value;
            ParseSeqParts(raw, out mods, out extras);
            if (entry != null) _seqCache[entry] = new KeyValuePair<string, List<KeyCode>>(raw, extras);
            return extras;
        }

        //完整序列 = 开头的修饰键（按固定顺序）+ 序列前键 + 主键
        public static List<KeyCode> SeqOf(ConfigEntry<KeyCode> entry) {
            List<KeyCode> seq = new List<KeyCode>();
            if (entry == null) return seq;
            ComboMod mods = KeyComboMod(entry);
            if ((mods & ComboMod.Shift) != 0) seq.Add(KeyCode.LeftShift);
            if ((mods & ComboMod.Ctrl) != 0) seq.Add(KeyCode.LeftControl);
            if ((mods & ComboMod.Alt) != 0) seq.Add(KeyCode.LeftAlt);
            seq.AddRange(SeqExtras(entry));
            KeyCode main = KeyCode.None;
            try { main = entry.Value; } catch { }
            if (main != KeyCode.None) seq.Add(main);
            return seq;
        }

        //保存"序列前键"（与修饰键一起写进同一个隐藏条目）
        private static void SetKeySeqExtra(ConfigEntryBase entry, List<KeyCode> extras) {
            if (entry == null) return;
            ComboMod mods = KeyComboMod(entry);
            string v = FormatComboMod(mods);
            if (extras != null) {
                foreach (KeyCode k in extras) {
                    if (k == KeyCode.None) continue;
                    v += (v.Length > 0 ? "+" : "") + k.ToString();
                }
            }
            ConfigEntry<string> me;
            if (!_keyModEntries.TryGetValue(entry, out me) || me == null) {
                if (_internalConfig != null) {
                    try {
                        me = _internalConfig.Bind("设置", "组合键 " + ComboEntryName(entry), "",
                            "组合键/序列（自动记录，如 Shift+Alpha9；最后一位是主键，存在键位项里）");
                        _keyModEntries[entry] = me;
                    } catch { me = null; }
                }
            }
            if (me != null) {
                try { if (me.Value != v) { me.Value = v; _dirty = true; } } catch (Exception __ex) { Guard.Log("写入按键序列", __ex); }
            }
            if (entry != null) _seqCache.Remove(entry);
        }

        //=== 序列匹配 ===（只在"有序列前键"时启用；纯修饰键组合仍走原来的 held 判定，行为不变）
        private static readonly Dictionary<ConfigEntryBase, int> _seqProg = new Dictionary<ConfigEntryBase, int>();
        private static readonly Dictionary<ConfigEntryBase, int> _seqStamp = new Dictionary<ConfigEntryBase, int>();
        private static readonly Dictionary<ConfigEntryBase, bool> _seqResult = new Dictionary<ConfigEntryBase, bool>();
        private static readonly List<KeyCode> _frameKeys = new List<KeyCode>();
        private static int _frameKeysStamp = -1;
        private static readonly List<KeyCode> _watchKeys = new List<KeyCode>();
        private static bool _watchDirty = true;

        //需要每帧轮询的按键集合 = 所有已登记键位的序列里出现过的键
        private static List<KeyCode> WatchKeys() {
            if (!_watchDirty) return _watchKeys;
            _watchDirty = false;
            _watchKeys.Clear();
            foreach (ConfigEntryBase e in _keyModEntries.Keys) {
                ConfigEntry<KeyCode> ke = e as ConfigEntry<KeyCode>;
                if (ke == null) continue;
                foreach (KeyCode k in SeqOf(ke)) if (k != KeyCode.None && !_watchKeys.Contains(k)) _watchKeys.Add(k);
            }
            foreach (ConfigEntryBase e in _keyMods.Keys) {
                ConfigEntry<KeyCode> ke = e as ConfigEntry<KeyCode>;
                if (ke == null) continue;
                foreach (KeyCode k in SeqOf(ke)) if (k != KeyCode.None && !_watchKeys.Contains(k)) _watchKeys.Add(k);
            }
            return _watchKeys;
        }

        //本帧按下的键（按 WatchKeys 顺序，一帧只算一次）
        private static List<KeyCode> FrameKeyDowns() {
            if (_frameKeysStamp == Time.frameCount) return _frameKeys;
            _frameKeysStamp = Time.frameCount;
            _frameKeys.Clear();
            List<KeyCode> watch = WatchKeys();
            for (int i = 0; i < watch.Count; i++) {
                try { if (Input.GetKeyDown(watch[i])) _frameKeys.Add(watch[i]); } catch { }
            }
            return _frameKeys;
        }

        //顺序匹配：按 seq 依次按下才算触发（同一 entry 每帧只算一次，避免一帧多次调用重复推进）
        private static bool SeqMatched(ConfigEntry<KeyCode> entry, List<KeyCode> seq) {
            int stamp;
            if (_seqStamp.TryGetValue(entry, out stamp) && stamp == Time.frameCount) {
                bool r; return _seqResult.TryGetValue(entry, out r) && r;
            }
            _seqStamp[entry] = Time.frameCount;
            _seqResult[entry] = false;
            List<KeyCode> down = FrameKeyDowns();
            if (down.Count == 0) return false;
            int prog;
            _seqProg.TryGetValue(entry, out prog);
            bool fired = false;
            for (int i = 0; i < down.Count; i++) {
                KeyCode k = down[i];
                if (prog < seq.Count && k == seq[prog]) {
                    prog++;
                    if (prog >= seq.Count) { fired = true; prog = 0; }
                } else if (k == seq[0]) {
                    prog = 1;
                } else {
                    prog = 0;
                }
            }
            _seqProg[entry] = prog;
            _seqResult[entry] = fired;
            return fired;
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
                        me = _internalConfig.Bind("设置", "组合键 " + ComboEntryName(entry), "",
                            "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                        _keyModEntries[entry] = me;
                    } catch { me = null; }
                }
            }
            if (me != null) {
                try {
                    //保留已有的"序列前键"，只更新修饰键部分（两者共用同一个隐藏条目）
                    List<KeyCode> keepExtras = SeqExtras(entry);
                    string v = FormatComboMod(mod);
                    foreach (KeyCode kx in keepExtras) { if (kx != KeyCode.None) v += (v.Length > 0 ? "+" : "") + kx.ToString(); }
                    if (me.Value != v) { me.Value = v; _dirty = true; }
                    _seqCache.Remove(entry);
                    _watchDirty = true;
                } catch (Exception __ex) { Guard.Log("写入按键修饰键", __ex); }
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
                } catch (Exception __ex) { Guard.Log("读取按键修饰键", __ex); }
            }
            SetKeyComboMod(entry, mod);
        }

        //当前条目需要的修饰键（无则 None）
        public static ComboMod KeyComboMod(ConfigEntryBase entry) {
            ComboMod m;
            if (entry != null && _keyMods.TryGetValue(entry, out m)) return m;
            return ComboMod.None;
        }

        //组合键匹配：要求的修饰键是否全部按下（None 恒 true；支持多修饰键，如 Shift+Ctrl）
        public static bool ComboModDown(ConfigEntryBase entry) {
            ComboMod need = KeyComboMod(entry);
            if (need == ComboMod.None) return true;
            ComboMod held = HeldComboMods();
            return (held & need) == need;
        }

        //按下检测：单键 / 修饰键组合（修饰键全部按住 + 主键按下）/ 多键序列（按顺序，如 9 → 0）
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            KeyCode main = KeyCode.None;
            try { main = entry.Value; } catch { }
            if (main == KeyCode.None) return false;
            //有"序列前键"→ 走顺序匹配（Shift → 9 → 0 之类）
            if (SeqExtras(entry).Count > 0) return SeqMatched(entry, SeqOf(entry));
            //原行为不变：修饰键（可多个）全部按住 + 主键按下
            return ComboModDown(entry) && Input.GetKeyDown(main);
        }

        //按住检测（含修饰键；序列键位按"主键按住"处理，供按住型功能使用）
        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            return ComboModDown(entry) && Input.GetKey(entry.Value);
        }

        //显示文本：单键 "X" / 组合键 "Shift+Ctrl + X" / 序列 "Shift + 9 → 0"
        public static string ComboKeyDisplay(ConfigEntryBase entry, KeyCode kc) {
            if (kc == KeyCode.None) return KeyDisplayName(kc);
            List<KeyCode> extras = SeqExtras(entry);
            string mods = FormatComboMod(KeyComboMod(entry));
            if (extras.Count == 0) return mods.Length > 0 ? mods + " + " + KeyDisplayName(kc) : KeyDisplayName(kc);
            string s = mods.Length > 0 ? mods + " + " : "";
            foreach (KeyCode kx in extras) s += KeyDisplayName(kx) + " → ";
            return s + KeyDisplayName(kc);
        }

	}
}
