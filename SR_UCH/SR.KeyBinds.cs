// 键位：组合键、录制式快捷键序列、右键绑键、序列匹配与统一轮询触发。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：KeyBinds（自定义键位 / 组合键 / 捕捉与显示）

        // 组合键持久化：每个键位建一个隐藏配置项存修饰键，重启后保留。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) {
            RegisterComboEntry(entry);
        }

        // 组合键条目名必须带分区名，否则 SR 与外部模块的同名键位会互相覆盖。
        private static string ComboEntryName(ConfigEntryBase entry) {
            return entry.Definition.Section + " " + entry.Definition.Key;
        }

        // 启动期（功能 Initialize）_internalConfig 未就绪时 Bind 不了隐藏条目：先记下，由 FlushPendingCombos 补建。
        private static readonly List<ConfigEntry<KeyCode>> _pendingCombo = new List<ConfigEntry<KeyCode>>();
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _pendingComboDefault = new Dictionary<ConfigEntryBase, ComboMod>();

        // 修饰键表以 ConfigEntryBase 对象为键，配置重读后旧对象会留成僵尸。
        // 超过 512 条时按现存条目裁剪；不能整体清空，否则已绑定的组合键全部失效。
        private static void PruneComboMapsIfNeeded() {
            try {
                if (_keyModEntries == null || _keyMods == null) return;
                if (_keyModEntries.Count <= 512) return;
                if (_internalConfig == null) return;
                System.Collections.Generic.HashSet<ConfigEntryBase> live =
                    new System.Collections.Generic.HashSet<ConfigEntryBase>();
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) live.Add(e);
                System.Collections.Generic.List<ConfigEntryBase> dead = new System.Collections.Generic.List<ConfigEntryBase>();
                foreach (ConfigEntryBase k in _keyModEntries.Keys) if (!live.Contains(k)) dead.Add(k);
                for (int i = 0; i < dead.Count; i++) { _keyModEntries.Remove(dead[i]); _keyMods.Remove(dead[i]); }
                if (dead.Count > 0) {
                    _watchDirty = true;
                    SRApi.LogInfo("[键位] 清理失效的组合键条目 " + dead.Count + " 条（剩余 " + _keyModEntries.Count + "）");
                }
            } catch (Exception __pc) { SRApi.Guard.Log("键位/清理失效条目", __pc); }
        }

        private static void RegisterComboEntry(ConfigEntry<KeyCode> entry) {
            if (entry == null || _keyModEntries.ContainsKey(entry)) return;
            PruneComboMapsIfNeeded();   // 条目重建后旧键会留成僵尸
            if (_internalConfig == null) {
                if (!_pendingCombo.Contains(entry)) _pendingCombo.Add(entry);
                return;
            }
            try {
                if (_internalConfig != null) {
                    ConfigEntry<string> modEntry = _internalConfig.Bind(Keys.Settings, Keys.ComboPrefix + ComboEntryName(entry), "",
                        "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                    // 旧命名迁移：新条目为空时搬入旧值
                    try {
                        if (string.IsNullOrEmpty(modEntry.Value)) {
                            ConfigEntry<string> legacy = _internalConfig.Bind(Keys.Settings, Keys.ComboPrefix + entry.Definition.Key, "",
                                "组合键修饰（旧命名，已弃用；值会自动迁移到带分区名的新条目）");
                            if (!string.IsNullOrEmpty(legacy.Value)) {
                                modEntry.Value = legacy.Value;
                                // 不清空旧条目：留着当备份，否则新条目被重置/删掉会丢失用户设置。
                            }
                        }
                    } catch (Exception __ex) { Guard.Log("RegisterComboEntry", __ex); }
                    _keyModEntries[entry] = modEntry;
                    _watchDirty = true;
                    NoteEntryBound();   // 让 SectionEntries 缓存失效
                    string v = modEntry.Value;
                    ComboMod m = ParseComboMod(v);
                    if (m != ComboMod.None) _keyMods[entry] = m;
                    else _keyMods.Remove(entry);
                }
            } catch (Exception __ex) { Guard.Log("建立按键组合条目", __ex); }
        }

        // 配置就绪后补建启动期注册的功能键组合条目，并应用被推迟的默认修饰键。
        public static void FlushPendingCombos() {
            if (_internalConfig == null) return;
            ConfigEntry<KeyCode>[] pend = _pendingCombo.ToArray();
            _pendingCombo.Clear();
            for (int i = 0; i < pend.Length; i++) RegisterComboEntry(pend[i]);
            if (_pendingComboDefault.Count == 0) return;
            List<KeyValuePair<ConfigEntryBase, ComboMod>> defs = new List<KeyValuePair<ConfigEntryBase, ComboMod>>();
            foreach (KeyValuePair<ConfigEntryBase, ComboMod> kv in _pendingComboDefault) defs.Add(kv);
            _pendingComboDefault.Clear();
            for (int i = 0; i < defs.Count; i++) {
                // 以持久化字符串是否为空为准；用 _keyMods 判会把延迟默认又写回去。
                ConfigEntry<string> __me;
                bool __has = _keyModEntries.TryGetValue(defs[i].Key, out __me) && __me != null && !string.IsNullOrEmpty(__me.Value);
                if (!__has) SetKeyComboMod(defs[i].Key, defs[i].Value);
            }
        }

        // 某些路径直接改键位 BoxedValue（重置默认值 / 右键清空）：让序列监听的 watch 列表失效。
        public static void MarkKeyChanged() { _watchDirty = true; _kcDirty = true; }

        // 解析持久化字符串：支持多修饰键 "Shift+Ctrl+Alt"（兼容单值 "Shift"/"Ctrl"/"Alt"）
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

        internal static string RawComboString(ConfigEntryBase entry) {
            try {
                ConfigEntry<string> me;
                if (entry != null && _keyModEntries.TryGetValue(entry, out me) && me != null) return me.Value ?? "";
            } catch (Exception __ex) { Guard.Log("RawComboString", __ex); }
            return "";
        }
        private static string RawModsInOrder(ConfigEntryBase entry) {
            try {
                string raw = RawComboString(entry);
                if (!string.IsNullOrEmpty(raw)) {
                    string[] parts = raw.Split(new char[] { '+' }, System.StringSplitOptions.RemoveEmptyEntries);
                    string s = "";
                    for (int i = 0; i < parts.Length; i++) {
                        string t = parts[i].Trim();
                        bool isMod = t == "Shift" || t == "Ctrl" || t == "Control" || t == "Alt";
                        if (!isMod) continue;
                        string disp = (t == "Control") ? "Ctrl" : t;
                        s += (s.Length > 0 ? " + " : "") + disp;
                    }
                    if (s.Length > 0) return s;
                }
            } catch (Exception __ex) { Guard.Log("RawModsInOrder", __ex); }
            return FormatComboMod(KeyComboMod(entry));   // 兜底：固定顺序
        }
        private static string FormatComboMod(ComboMod m) {
            if (m == ComboMod.None) return "";
            string s = "";
            if ((m & ComboMod.Shift) != 0) s += "Shift";
            if ((m & ComboMod.Ctrl) != 0) s += (s.Length > 0 ? "+" : "") + "Ctrl";
            if ((m & ComboMod.Alt) != 0) s += (s.Length > 0 ? "+" : "") + "Alt";
            return s;
        }

        // 按住中的修饰键按帧缓存（同帧结果必相同）；GetKeyDown 是边沿触发，走 ComboDown。
        private static ComboMod _heldModsCache;
        private static int _heldModsFrame = -1;
        public static ComboMod HeldComboMods() {
            if (_heldModsFrame == Time.frameCount) return _heldModsCache;
            _heldModsFrame = Time.frameCount;
            ComboMod m = ComboMod.None;
            try {
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= ComboMod.Shift;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= ComboMod.Ctrl;
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= ComboMod.Alt;
            } catch (Exception __ex) { Guard.Log("HeldComboMods", __ex); }
            _heldModsCache = m;
            return m;
        }

        // 录制式快捷键：一串按键 + 顺序（如 Shift -> 9 -> 0）；隐藏条目存修饰键 + 序列前键。

        private static bool IsModifierKey(KeyCode k) {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift ||
                   k == KeyCode.LeftControl || k == KeyCode.RightControl ||
                   k == KeyCode.LeftAlt || k == KeyCode.RightAlt;
        }

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

        private static KeyCode ParseKeyName(string p) {
            if (string.IsNullOrEmpty(p)) return KeyCode.None;
            if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftShift;
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftControl;
            if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftAlt;
            try {
                if (Enum.IsDefined(typeof(KeyCode), p)) return (KeyCode)Enum.Parse(typeof(KeyCode), p, true);
            } catch (Exception __ex) { Guard.Log("ParseKeyName", __ex); }
            return KeyCode.None;
        }

        // 序列前键（Extras）：从隐藏条目解析（缓存键 = 字符串 + 主键）
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

        public static List<KeyCode> SeqOf(ConfigEntry<KeyCode> entry) {
            List<KeyCode> seq = new List<KeyCode>();
            if (entry == null) return seq;
            ComboMod mods = KeyComboMod(entry);
            if ((mods & ComboMod.Shift) != 0) seq.Add(KeyCode.LeftShift);
            if ((mods & ComboMod.Ctrl) != 0) seq.Add(KeyCode.LeftControl);
            if ((mods & ComboMod.Alt) != 0) seq.Add(KeyCode.LeftAlt);
            seq.AddRange(SeqExtras(entry));
            KeyCode main = KeyCode.None;
            try { main = entry.Value; } catch (Exception __ex) { Guard.Log("SeqOf", __ex); }
            if (main != KeyCode.None) seq.Add(main);
            return seq;
        }

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
                        me = _internalConfig.Bind(Keys.Settings, Keys.ComboPrefix + ComboEntryName(entry), "",
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

        // 序列匹配（仅"有序列前键"时启用；纯修饰键组合走 held 判定）
        private static readonly Dictionary<ConfigEntryBase, int> _seqProg = new Dictionary<ConfigEntryBase, int>();
        // 序列进度时间戳：超过 1.2 秒没有新按键就作废，避免半完成进度残留。
        private static readonly Dictionary<ConfigEntryBase, float> _seqAt = new Dictionary<ConfigEntryBase, float>();
        private static readonly Dictionary<ConfigEntryBase, int> _seqStamp = new Dictionary<ConfigEntryBase, int>();
        private static readonly Dictionary<ConfigEntryBase, bool> _seqResult = new Dictionary<ConfigEntryBase, bool>();
        private static readonly Dictionary<ConfigEntryBase, int> _comboStamp = new Dictionary<ConfigEntryBase, int>();
        private static readonly Dictionary<ConfigEntryBase, bool> _comboResult = new Dictionary<ConfigEntryBase, bool>();
        private static readonly List<KeyCode> _frameKeys = new List<KeyCode>();
        private static int _frameKeysStamp = -1;
        private static readonly List<KeyCode> _watchKeys = new List<KeyCode>();
        private static bool _watchDirty = true;

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

        private static List<KeyCode> FrameKeyDowns() {
            if (_frameKeysStamp == Time.frameCount) return _frameKeys;
            _frameKeysStamp = Time.frameCount;
            _frameKeys.Clear();
            List<KeyCode> watch = WatchKeys();
            for (int i = 0; i < watch.Count; i++) {
                try { if (Input.GetKeyDown(watch[i])) _frameKeys.Add(watch[i]); } catch (Exception __ex) { Guard.Log("FrameKeyDowns", __ex); }
            }
            return _frameKeys;
        }

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
            float __lastAt;
            if (prog != 0) {
                _seqAt.TryGetValue(entry, out __lastAt);
                if (Time.unscaledTime - __lastAt > 1.2f) prog = 0;
            }
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
            if (prog != 0) {
                _seqProg[entry] = prog;
                _seqAt[entry] = Time.unscaledTime;
            } else {
                _seqProg.Remove(entry);
                _seqAt.Remove(entry);
            }
            // 上限兜底：条目被重建后旧键不会自己消失，超过 256 条整体清空（键位状态本来就每帧可重建）。
            if (_seqProg.Count > 256) { _seqProg.Clear(); _seqAt.Clear(); _seqStamp.Clear(); _seqResult.Clear(); }
            _seqResult[entry] = fired;
            return fired;
        }

        internal static void SetRawComboString(ConfigEntryBase entry, string text) {
            if (entry == null) return;
            try {
                RegisterComboEntry((ConfigEntry<KeyCode>)entry);
                ConfigEntry<string> me;
                if (_keyModEntries.TryGetValue(entry, out me) && me != null && me.Value != text) { me.Value = text; _dirty = true; }
                _seqCache.Remove(entry);
            } catch (Exception __ex) { Guard.Log("写入组合串", __ex); }
        }
        private static void SetKeyComboMod(ConfigEntryBase entry, ComboMod mod) {
            if (entry == null) return;
            if (mod == ComboMod.None) _keyMods.Remove(entry);
            else _keyMods[entry] = mod;
            ConfigEntry<string> me;
            if (!_keyModEntries.TryGetValue(entry, out me) || me == null) {
                // 未预注册（如 External 键位或 _internalConfig 尚未就绪）：此时补建持久化配置
                if (_internalConfig != null) {
                    try {
                        me = _internalConfig.Bind(Keys.Settings, Keys.ComboPrefix + ComboEntryName(entry), "",
                            "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                        _keyModEntries[entry] = me;
                    } catch { me = null; }
                }
            }
            if (me != null) {
                try {
                    // 保留已有的序列前键，只更新修饰键部分（两者共用同一个隐藏条目）
                    List<KeyCode> keepExtras = SeqExtras(entry);
                    string v = FormatComboMod(mod);
                    foreach (KeyCode kx in keepExtras) { if (kx != KeyCode.None) v += (v.Length > 0 ? "+" : "") + kx.ToString(); }
                    if (me.Value != v) { me.Value = v; _dirty = true; }
                    _seqCache.Remove(entry);
                    _watchDirty = true;
                } catch (Exception __ex) { Guard.Log("写入按键修饰键", __ex); }
            }
        }

        public static void SetDefaultCombo(ConfigEntryBase entry, ComboMod mod) {
            if (entry == null || _keyMods.ContainsKey(entry)) return;
            if (_internalConfig == null) {
                // 配置未就绪：推迟到 FlushPendingCombos（cfg 已有修饰键时不覆盖）
                _pendingComboDefault[entry] = mod;
                return;
            }
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

        public static ComboMod KeyComboMod(ConfigEntryBase entry) {
            ComboMod m;
            if (entry != null && _keyMods.TryGetValue(entry, out m)) return m;
            return ComboMod.None;
        }

        public static bool ComboModDown(ConfigEntryBase entry) {
            ComboMod need = KeyComboMod(entry);
            if (need == ComboMod.None) return true;
            ComboMod held = HeldComboMods();
            return (held & need) == need;
        }

        // 按下检测：单键 / 组合键 / 多键序列；Typing 为真时一律返回 false；所有键轮询都走这里。
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            if (Typing) return false;  // Typing 由 ChatWindow 注册，核心不编译期引用它
            KeyCode main = KeyCode.None;
            try { main = entry.Value; } catch (Exception __ex) { Guard.Log("ComboKeyDown", __ex); }
            if (main == KeyCode.None) return false;
            if (SeqExtras(entry).Count > 0) return SeqMatched(entry, SeqOf(entry));
            // 按帧去重：Input.GetKeyDown 整帧为 true 而 FixedUpdate 一帧可多跑，故按 entry 缓存。
            int stamp;
            if (_comboStamp.TryGetValue(entry, out stamp) && stamp == Time.frameCount) {
                bool cached; return _comboResult.TryGetValue(entry, out cached) && cached;
            }
            // Input 主通道；未命中时再吃一次 IMGUI 捕获的按键（游戏界面吞输入时更跟手）。
            bool res = ComboModDown(entry) && (Input.GetKeyDown(main) || ConsumeGuiKey(main));
            // 与 _seqProg 同一套上限：配置重载后条目对象会重建，旧键会留成僵尸。
            if (_comboStamp.Count > 256) { _comboStamp.Clear(); _comboResult.Clear(); }
            _comboStamp[entry] = Time.frameCount;
            _comboResult[entry] = res;
            return res;
        }

        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            if (Typing) return false;  // Typing 由 ChatWindow 注册，核心不编译期引用它
            return ComboModDown(entry) && Input.GetKey(entry.Value);
        }

        public static string ComboKeyDisplay(ConfigEntryBase entry, KeyCode kc) {
            if (kc == KeyCode.None) return KeyDisplayName(kc);
            List<KeyCode> extras = SeqExtras(entry);
            // 修饰键按按下顺序显示（原始组合串保存的就是按下顺序），不用固定顺序
            string mods = RawModsInOrder(entry);
            bool __kcIsMod = false;
            try {
                __kcIsMod = (kc == KeyCode.LeftControl || kc == KeyCode.RightControl
                    || kc == KeyCode.LeftShift || kc == KeyCode.RightShift
                    || kc == KeyCode.LeftAlt || kc == KeyCode.RightAlt
                    || kc == KeyCode.LeftCommand || kc == KeyCode.RightCommand);
            } catch (Exception __ex) { Guard.Log("ComboKeyDisplay", __ex); }
            if (__kcIsMod) {
                if (extras.Count > 0) { string __s2 = KeyDisplayName(kc) + " + "; foreach (KeyCode kx2 in extras) __s2 += KeyDisplayName(kx2) + " -> "; return __s2; }
                if (mods.Length > 0) return KeyDisplayName(kc) + " + " + mods;
                return KeyDisplayName(kc);
            }
            if (extras.Count == 0) return mods.Length > 0 ? mods + " + " + KeyDisplayName(kc) : KeyDisplayName(kc);
            string s = mods.Length > 0 ? mods + " + " : "";
            foreach (KeyCode kx in extras) s += KeyDisplayName(kx) + " -> ";
            return s + KeyDisplayName(kc);
        }

        // 自动快捷键：右键开关/按钮绑定；条目建在隐藏段 [Hotkeys]，由 CheckHotkeys 每帧检测。
        private const string HkSec = "Hotkeys";

        private sealed class HkItem {
            public string Id;
            public string Label;
            public ConfigEntry<KeyCode> Key;
            public Action Run;         //按下即执行
        }

        private static readonly List<HkItem> _hkItems = new List<HkItem>();
        private static readonly Dictionary<string, HkItem> _hkById = new Dictionary<string, HkItem>();
        private static bool _hkInitDone;

        private static HkItem HkEnsure(string id, string label) {
            if (string.IsNullOrEmpty(id)) return null;
            HkItem it;
            if (_hkById.TryGetValue(id, out it)) { if (!string.IsNullOrEmpty(label)) it.Label = label; return it; }
            it = new HkItem { Id = id, Label = label };
            _hkById[id] = it;
            _hkItems.Add(it);
            return it;
        }

        public sealed class KeyConflict {
            public string A, B;
            public string Combo;
        }

        // 收集当前所有自动快捷键的冲突（同键 + 同修饰键才算冲突；None 忽略）。
        public static List<KeyConflict> FindKeyConflicts() {
            List<KeyConflict> res = new List<KeyConflict>();
            try {
                HkInitAll();
                for (int i = 0; i < _hkItems.Count; i++) {
                    HkItem a = _hkItems[i];
                    if (a == null || a.Key == null || a.Key.Value == KeyCode.None) continue;
                    for (int j = i + 1; j < _hkItems.Count; j++) {
                        HkItem b = _hkItems[j];
                        if (b == null || b.Key == null || b.Key.Value == KeyCode.None) continue;
                        if (a.Key.Value != b.Key.Value) continue;
                        if (ComboModOf(a.Key) != ComboModOf(b.Key)) continue;
                        res.Add(new KeyConflict { A = a.Label ?? a.Id, B = b.Label ?? b.Id, Combo = ComboNameOf(a.Key) });
                    }
                }
            } catch (Exception __ex) { Guard.Log("键位冲突检测", __ex); }
            return res;
        }

        private static int ComboModOf(ConfigEntry<KeyCode> e) {
            try {
                ComboMod m;
                if (_keyMods != null && _keyMods.TryGetValue(e, out m)) return (int)m;
            } catch (Exception __ex) { Guard.Log("键位冲突/读修饰键", __ex); }
            return 0;
        }

        private static string ComboNameOf(ConfigEntry<KeyCode> e) {
            try { return FormatComboMod(KeyComboMod(e)) + KeyDisplayName(e.Value); }
            catch (Exception __ex) { Guard.Log("键位冲突/组合键文本", __ex); return e != null ? e.Value.ToString() : ""; }
        }

        // 登记"按下即执行"的按钮动作（Initialize 或页面渲染时调用均可，重复登记无副作用）
        public static void HotkeyAction(string id, string label, Action action) {
            HkItem it = HkEnsure(id, label);
            if (it != null) { it.Run = action; }
        }

        public static ConfigEntry<KeyCode> Hotkey(string id) {
            HkItem it;
            if (string.IsNullOrEmpty(id) || !_hkById.TryGetValue(id, out it)) return null;
            if (it.Key == null && _internalConfig != null) {
                try {
                    it.Key = _internalConfig.Bind(HkSec, it.Id, KeyCode.None,
                        "自动快捷键：" + (it.Label ?? it.Id) + "（在管理界面右键对应开关/按钮绑定，再右键删除）");
                    RegisterComboEntry(it.Key);
                    NoteEntryBound();   // 让 SectionEntries 缓存失效
                    _watchDirty = true;
                } catch (Exception __ex) { Guard.Log("建立自动快捷键条目", __ex); }
            }
            return it.Key;
        }

        // 显示文本（"" = 未绑定）：界面拼在标签后；录制中返回紧凑的 [...] 文本。
        public static string HotkeyText(string id) {
            ConfigEntry<KeyCode> k = Hotkey(id);
            if (k == null) return "";
            return KeySuffix(k);
        }

        // 供核心控件调用的公开入口（进入录制）。
        internal static void EnterCapturePublic(ConfigEntry<KeyCode> k) { SR.EnterCaptureInternal(k); }

        public static string KeyText(ConfigEntry<KeyCode> k) {
            if (k == null) return T("未绑定", "Unbound");
            if (_capturing == k) return _recSeq.Count == 0 ? "..." : RecText();
            KeyCode v = KeyCode.None;
            try { v = k.Value; } catch (Exception __ex) { Guard.Log("KeyText", __ex); }
            if (v == KeyCode.None) return T("未绑定", "Unbound");
            return ComboKeyDisplay(k, v);
        }

        // 键位后缀（"" = 未绑定）：统一给标签 / 按钮拼文本，录制中显示紧凑的 [...] / [正在录的键]
        public static string KeySuffix(ConfigEntry<KeyCode> k) {
            if (k == null) return "";
            if (_capturing == k) return _recSeq.Count == 0 ? " [...]" : " [" + RecText() + "]";
            KeyCode v = KeyCode.None;
            try { v = k.Value; } catch (Exception __ex) { Guard.Log("KeySuffix", __ex); }
            if (v == KeyCode.None) return "";
            return " [" + ComboKeyDisplay(k, v) + "]";
        }

        // 把已登记的条目都建出来（首次按键检查时；旧绑定从 cfg 读回）
        private static void HkInitAll() {
            if (_hkInitDone || _internalConfig == null) return;
            _hkInitDone = true;
            try { NavHide(HkSec); } catch (Exception __ex) { Guard.Log("隐藏 Hotkeys 段", __ex); }
            for (int i = 0; i < _hkItems.Count; i++) {
                try { Hotkey(_hkItems[i].Id); } catch (Exception __ex) { Guard.Log("建立自动快捷键 " + _hkItems[i].Id, __ex); }
            }
        }

        // 每帧轮询（SR.Tick 调用）：绑定的键按下 -> 执行动作
        private sealed class KeyAct {
            public string Name, Mode;
            public ConfigEntry<KeyCode> Entry;
            public Action Run;
            public Type Impl;        // 受限功能的实现类型（null = 不受模式限制）
        }
        private static readonly List<KeyAct> _keyActs = new List<KeyAct>();

        public static void RegisterKeyAction(string name, ConfigEntry<KeyCode> entry, string mode, Action action) {
            RegisterKeyAction(name, entry, mode, action, null);
        }

        // impl 非空 = 受模式限制：核心在派发前统一判定，不通过则不执行并给节流提示。
        public static void RegisterKeyAction(string name, ConfigEntry<KeyCode> entry, string mode, Action action, Type impl) {
            if (entry == null || action == null) return;
            for (int i = 0; i < _keyActs.Count; i++)
                if (ReferenceEquals(_keyActs[i].Entry, entry)) {
                    _keyActs[i].Mode = mode ?? "press"; _keyActs[i].Run = action; _keyActs[i].Name = name; _keyActs[i].Impl = impl; return;
                }
            KeyAct a = new KeyAct(); a.Name = name; a.Mode = mode ?? "press"; a.Entry = entry; a.Run = action; a.Impl = impl;
            _keyActs.Add(a);
        }

        private static void CheckRegisteredKeyActions() {
            for (int i = 0; i < _keyActs.Count; i++) {
                KeyAct a = _keyActs[i];
                try {
                    if (a.Entry == null) continue;
                    bool fire = (a.Mode == "hold") ? ComboKeyHeld(a.Entry) : ComboKeyDown(a.Entry);
                    if (!fire || a.Run == null) continue;
                    // 模式限制统一在这里判：受限功能在错误模式下不执行，并给一次提示。
                    if (a.Impl != null && !GateFeatureForAction(a.Impl, a.Name)) continue;
                    a.Run();
                } catch (Exception __ex) { Guard.Log("键位动作 " + (a.Name ?? "?"), __ex); }
            }
        }

        public static void CheckHotkeys() {
            // 每个条目的回调单独隔离：一个功能抛异常不影响其余快捷键。
            // 注意：本方法由 ManagerUI.Update 每帧调用，与主面板是否打开无关 —— 独立窗口的快捷键靠这条链路。
            try { HkInitAll(); } catch (Exception __ex) { Guard.Log("初始化自动快捷键", __ex); }
            CheckRegisteredKeyActions();   // 功能登记的键位动作（核心统一轮询；hold 类需每帧）
            if (_capturing != null) return; // 正在录制按键：不触发
            // 本帧无按键事件时不必遍历快捷键；hold 语义的键位动作已在上一行统一处理。
            if (!UnityEngine.Input.anyKeyDown && !SR.GuiKeyThisFrame) return;
            for (int i = 0; i < _hkItems.Count; i++) {
                HkItem it = _hkItems[i];
                try {
                    if (it == null) continue;
                // 每帧补一次惰性 Bind：HkInitAll 只跑一次，晚登记的 id 其 Key 会一直为 null。
                if (it.Key == null) { try { Hotkey(it.Id); } catch (Exception __b) { Guard.Log("惰性建立快捷键 " + it.Id, __b); } }
                if (it.Key == null || it.Key.Value == KeyCode.None) continue;
                    if (!ComboKeyDown(it.Key)) continue;
                    if (it.Run != null) it.Run();
                } catch (Exception __ex) { Guard.Log("自动快捷键 " + (it != null ? it.Id : "?"), __ex); }
            }
        }

        // 分区：按键中心（自检页卡片；自动快捷键 + 手动键位，支持清除与重复提示）
        private sealed class KeyCenterRow {
            public ConfigEntry<KeyCode> Entry;
            public string Group;    // 分组显示名（本地化）
            public string Name;     // 条目显示名
            public string Combo;    // "Ctrl+K" / "未绑定"
            public bool Dup;        // 与其它条目重复
            public bool Auto;       // 自动快捷键
        }

        private static List<KeyCenterRow> _kcRows;
        private static int _kcBuiltEntryVersion = -1;
        private static bool _kcDirty = true;
        internal static void MarkKeyCenterDirty() { _kcDirty = true; }
        // 只读数量，供其它分部文件与自检页使用（分区守卫要求不直接碰私有字段）。
        internal static int HotkeyCount { get { return _hkItems != null ? _hkItems.Count : 0; } }

        private static string KeyCenterAutoLabel(string id) {
            HkItem it;
            if (!string.IsNullOrEmpty(id) && _hkById.TryGetValue(id, out it) && it != null)
                return it.Label ?? it.Id;
            return id;
        }

        private static void BuildKeyCenterRows() {
            _kcRows = new List<KeyCenterRow>();
            try {
                if (_internalConfig == null) return;
                _kcBuiltEntryVersion = EntryVersion;
                _kcDirty = false;
                foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                    ConfigEntry<KeyCode> kc = e as ConfigEntry<KeyCode>;
                    if (kc == null) continue;
                    KeyCenterRow r = new KeyCenterRow();
                    r.Entry = kc;
                    r.Auto = string.Equals(e.Definition.Section, HkSec, StringComparison.Ordinal);
                    r.Group = r.Auto ? T("自动快捷键", "Auto hotkeys") : ZhSection(e.Definition.Section);
                    r.Name = r.Auto ? KeyCenterAutoLabel(e.Definition.Key) : ZhKey(kc);
                    r.Combo = ComboKeyDisplay(kc, kc.Value);
                    _kcRows.Add(r);
                }
                // 重复组合：同一 KeyCode + 同一修饰键（忽略未绑定）
                for (int i = 0; i < _kcRows.Count; i++) {
                    if (_kcRows[i].Entry.Value == KeyCode.None) continue;
                    ComboMod mi = KeyComboMod(_kcRows[i].Entry);
                    for (int j = i + 1; j < _kcRows.Count; j++) {
                        if (_kcRows[j].Entry.Value != _kcRows[i].Entry.Value) continue;
                        if (KeyComboMod(_kcRows[j].Entry) != mi) continue;
                        _kcRows[i].Dup = true;
                        _kcRows[j].Dup = true;
                    }
                }
                _kcRows.Sort(delegate (KeyCenterRow a, KeyCenterRow b) {
                    int c = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
                    return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
            } catch (Exception __ex) { Guard.Log("按键中心/采集", __ex); }
        }

        internal static void RenderKeyCenterCard() {
            try {
                if (_internalConfig == null) return;
                if (_kcRows == null || _kcDirty || _kcBuiltEntryVersion != EntryVersion) BuildKeyCenterRows();
                int bound = 0, autoBound = 0, dupRows = 0;
                for (int i = 0; i < _kcRows.Count; i++) {
                    if (_kcRows[i].Entry.Value != KeyCode.None) { bound++; if (_kcRows[i].Auto) autoBound++; }
                    if (_kcRows[i].Dup) dupRows++;
                }
                string summary = T("已绑定 " + bound + " 个（自动 " + autoBound + "）· 重复组合 " + dupRows + " 条",
                                   bound + " bound (" + autoBound + " auto) · " + dupRows + " duplicate row(s)");
                Card(dupRows == 0, T("按键中心", "Hotkey center"), summary, delegate {
                    if (GUILayout.Button(T("重新读取键位", "Reload bindings"), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(24)))) {
                        MarkKeyCenterDirty();
                        BuildKeyCenterRows();
                    }
                    GUILayout.Space(Sc(4));
                    if (_kcRows.Count == 0) { AuditLabel(T("（没有可显示的按键条目）", "(no key entries)")); return; }
                    string lastGroup = null;
                    for (int i = 0; i < _kcRows.Count; i++) {
                        KeyCenterRow r = _kcRows[i];
                        if (r.Group != lastGroup) { lastGroup = r.Group; AuditLabel("- " + (lastGroup ?? "") + " -"); }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label((r.Dup ? "⚠ " : "") + r.Name + "  [" + r.Combo + "]", _label,
                            GUILayout.Width(Sc(420)), GUILayout.Height(Ctl.TableRowH()));
                        GUILayout.FlexibleSpace();
                        bool prevEn = GUI.enabled;
                        try {
                            GUI.enabled = prevEn && r.Entry.Value != KeyCode.None;
                            if (GUILayout.Button(T("清除", "Clear"), _btn, GUILayout.Width(Sc(64)), GUILayout.Height(Sc(22)))) {
                                r.Entry.Value = KeyCode.None;
                                MarkKeyChanged();
                                MarkKeyCenterDirty();
                                Notify(T("已清除按键", "Hotkey cleared"));
                            }
                        } finally { GUI.enabled = prevEn; }
                        GUILayout.EndHorizontal();
                    }
                    AuditLabel(T("说明：自动快捷键来自各功能页面的按钮，右键可绑定；手动键位在对应功能页里右键绑定。"
                                 + "这里是全局清单，清除后回原页面可重新绑定。",
                                 "Auto hotkeys come from feature-page buttons (right-click to bind); manual keys are bound on their own pages. "
                                 + "Clear here and rebind on the original page."));
                });
            } catch (Exception __ex) { Guard.Log("自检/按键中心", __ex); }
        }
    }
}
