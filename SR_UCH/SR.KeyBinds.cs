// 键位。KeyboardShortcut 风格的组合键（按按下顺序显示）、一串按键组成的录制式快捷键、
// 右键绑键、序列匹配，以及统一的轮询触发。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：KeyBinds（自定义键位 / Shift·Ctrl·Alt 组合键 / 捕捉与显示）

        //组合键持久化：每个键位建一个隐藏配置项（设置\组合键 <Key>）存修饰键，重启后仍保留。
        //
        // 原有两个入口（RegisterKey / RegisterShiftKey）**方法体完全相同**，name 与 mode 参数都被忽略：
        //  RegisterShiftKey 是"只支持 Shift 修饰"那版的遗留，而组合键机制早已泛化成
        //  Shift/Ctrl/Alt 任意组合 + 按键序列（见 ParseComboMod / ParseSeqParts），名字反而误导。
        //  现在合并成一个 RegisterKey（原 RegisterShiftKey 的唯一调用点已改用本方法）。
        //  name / mode 仍保留在签名里：调用点已按此签名写好，且将来若要按 mode 区分
        //  "press / hold / toggle" 语义可直接扩展，不必再改所有调用点。
        public static void RegisterKey(string name, ConfigEntry<KeyCode> entry, string mode) {
            RegisterComboEntry(entry);
        }

        //组合键条目名必须带分区名：SR 与外部模块都有 "Respawn Key"，只用 Key 名会互相覆盖。
        private static string ComboEntryName(ConfigEntryBase entry) {
            return entry.Definition.Section + " " + entry.Definition.Key;
        }

        //启动期（功能 Initialize）_internalConfig 还没就绪：此时 Bind 不了隐藏条目，
        //先记下来，等配置就绪后由 FlushPendingCombos 补建。否则重启后组合键/序列全丢。
        private static readonly List<ConfigEntry<KeyCode>> _pendingCombo = new List<ConfigEntry<KeyCode>>();
        private static readonly Dictionary<ConfigEntryBase, ComboMod> _pendingComboDefault = new Dictionary<ConfigEntryBase, ComboMod>();

        //建立持久化修饰键配置（惰性：捕捉时首次设置才写入）
        private static void RegisterComboEntry(ConfigEntry<KeyCode> entry) {
            if (entry == null || _keyModEntries.ContainsKey(entry)) return;
            if (_internalConfig == null) {
                if (!_pendingCombo.Contains(entry)) _pendingCombo.Add(entry);
                return;
            }
            try {
                if (_internalConfig != null) {
                    ConfigEntry<string> modEntry = _internalConfig.Bind("Settings", "组合键 " + ComboEntryName(entry), "",
                        "组合键修饰（自动记录，Shift/Ctrl/Alt/空）");
                    //旧命名迁移：新条目为空时搬入旧值，避免升级后丢组合键
                    try {
                        if (string.IsNullOrEmpty(modEntry.Value)) {
                            ConfigEntry<string> legacy = _internalConfig.Bind("Settings", "组合键 " + entry.Definition.Key, "",
                                "组合键修饰（旧命名，已弃用；值会自动迁移到带分区名的新条目）");
                            if (!string.IsNullOrEmpty(legacy.Value)) {
                                modEntry.Value = legacy.Value;
                                legacy.Value = "";
                            }
                        }
                    } catch (Exception __ex) { Guard.Log("RegisterComboEntry", __ex); }
                    _keyModEntries[entry] = modEntry;
                    _watchDirty = true;
                    NoteEntryBound();   //新增了一个条目：让 SectionEntries 的缓存失效
                    string v = modEntry.Value;
                    ComboMod m = ParseComboMod(v);
                    if (m != ComboMod.None) _keyMods[entry] = m;
                    else _keyMods.Remove(entry);
                }
            } catch (Exception __ex) { Guard.Log("建立按键组合条目", __ex); }
        }

        //配置就绪后补建启动期注册的功能键组合条目，并应用被推迟的默认修饰键。
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
                if (!_keyMods.ContainsKey(defs[i].Key)) SetKeyComboMod(defs[i].Key, defs[i].Value);
            }
        }

        //某些路径直接改键位 BoxedValue（重置默认值 / 右键清空）：让序列监听的 watch 列表失效。
        public static void MarkKeyChanged() { _watchDirty = true; }

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

        // 读该条目保存的"组合/序列"原始串（录制时按按下顺序写入）
        internal static string RawComboString(ConfigEntryBase entry) {
            try {
                ConfigEntry<string> me;
                if (entry != null && _keyModEntries.TryGetValue(entry, out me) && me != null) return me.Value ?? "";
            } catch (Exception __ex) { Guard.Log("RawComboString", __ex); }
            return "";
        }
        // 按"按下顺序"返回修饰键串（如 "Alt+Shift"）。原始组合串由录制时按按下顺序写入。
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
            return FormatComboMod(KeyComboMod(entry));   //兜底：固定顺序
        }
        private static string FormatComboMod(ComboMod m) {
            if (m == ComboMod.None) return "";
            string s = "";
            if ((m & ComboMod.Shift) != 0) s += "Shift";
            if ((m & ComboMod.Ctrl) != 0) s += (s.Length > 0 ? "+" : "") + "Ctrl";
            if ((m & ComboMod.Alt) != 0) s += (s.Length > 0 ? "+" : "") + "Alt";
            return s;
        }

        //按住中的修饰键：同一帧内结果必然相同，而它经 ComboModDown -> ComboKeyDown/ComboKeyHeld 被全项目
        //42 处调用点使用（每帧约 100 次）-> 按帧缓存，避免每帧上百次 Input.GetKey。
        //注意：GetKeyDown 是边沿触发，**不能**按帧缓存（那条路径是 ComboDown，不走这里）。
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

        // 录制式快捷键：一串按键 + 顺序（如 Shift -> 9 -> 0）
        //持久化仍是一条字符串（与组合键共用同一隐藏条目）：开头连续修饰键 + 序列前键，最后一位是主键（存在键位项里）。

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

        //名称 -> KeyCode：支持 Enum 名（"Alpha9"/"LeftShift"）与简写（"Shift"/"Ctrl"/"Alt"）
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
                        me = _internalConfig.Bind("Settings", "组合键 " + ComboEntryName(entry), "",
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

        // 序列匹配 ===（仅"有序列前键"时启用；纯修饰键组合仍走 held 判定）
        private static readonly Dictionary<ConfigEntryBase, int> _seqProg = new Dictionary<ConfigEntryBase, int>();
        private static readonly Dictionary<ConfigEntryBase, int> _seqStamp = new Dictionary<ConfigEntryBase, int>();
        private static readonly Dictionary<ConfigEntryBase, bool> _seqResult = new Dictionary<ConfigEntryBase, bool>();
        //普通组合键（修饰键+主键）的按帧去重：同 SeqMatched 的那套，见 ComboKeyDown 里的说明。
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

        //本帧按下的键（按 WatchKeys 顺序，一帧只算一次）
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

        // **顺序修复**：直接写入"原始组合串"（录制时按按下顺序生成；解析按名字，不依赖顺序）
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
                //未预注册（如 External 键位或 _internalConfig 尚未就绪）：此时补建持久化配置
                if (_internalConfig != null) {
                    try {
                        me = _internalConfig.Bind("Settings", "组合键 " + ComboEntryName(entry), "",
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
            if (_internalConfig == null) {
                //配置未就绪：推迟到 FlushPendingCombos（届时若 cfg 已有修饰键则不覆盖用户设置）
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

        //按下检测：单键 / 修饰键组合（修饰键全部按住 + 主键按下）/ 多键序列（按顺序，如 9 -> 0）
        //注：游戏自己的聊天输入框打开时（Typing）一律返回 false, 打字期间不能触发任何功能快捷键
        //（所有 SR 功能与外部模块的键轮询都走这两个函数，这里是唯一的统一入口）。
        public static bool ComboKeyDown(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            if (Typing) return false;  //聊天框是否在输入：由 ChatWindow 注册（核心不编译期引用它）
            KeyCode main = KeyCode.None;
            try { main = entry.Value; } catch (Exception __ex) { Guard.Log("ComboKeyDown", __ex); }
            if (main == KeyCode.None) return false;
            if (SeqExtras(entry).Count > 0) return SeqMatched(entry, SeqOf(entry));
            //按帧去重：Input.GetKeyDown 在整个渲染帧内都返回 true，而 FixedUpdate 一帧会跑多次
            //（60fps 偶尔 2 次，30fps 多数帧 2 次）-> 挂在 FixedUpdate 上的调用点（如快速自杀
            //的 Character.FixedUpdate postfix）会在同一帧被判定若干次，一次按键触发多遍。
            //同一帧内"这个键是不是刚按下"答案必然相同，故按 entry + frameCount 缓存结果。
            //（序列键走 SeqMatched，它自己已有同样的按帧去重。）
            int stamp;
            if (_comboStamp.TryGetValue(entry, out stamp) && stamp == Time.frameCount) {
                bool cached; return _comboResult.TryGetValue(entry, out cached) && cached;
            }
            bool res = ComboModDown(entry) && Input.GetKeyDown(main);
            _comboStamp[entry] = Time.frameCount;
            _comboResult[entry] = res;
            return res;
        }

        public static bool ComboKeyHeld(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            if (Typing) return false;  //聊天框是否在输入：由 ChatWindow 注册（核心不编译期引用它）
            return ComboModDown(entry) && Input.GetKey(entry.Value);
        }

        //显示文本：单键 "X" / 组合键 "Shift+Ctrl + X" / 序列 "Shift + 9 -> 0"
        public static string ComboKeyDisplay(ConfigEntryBase entry, KeyCode kc) {
            if (kc == KeyCode.None) return KeyDisplayName(kc);
            List<KeyCode> extras = SeqExtras(entry);
            // **顺序修复**：修饰键按"按下顺序"显示（原始组合串里保存的就是按下顺序），不再用固定顺序
            string mods = RawModsInOrder(entry);
            // **#4 修正组合键显示顺序**：录制时若把"修饰键"存进了主键位（表现为 Ctrl+Z 显示成 Z+Ctrl），
            //   这里检测主键位是否为修饰键，是则把修饰键放到最前面显示。
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

        // 自动快捷键：右键界面上的任意开关/按钮即可绑定，SR 统一轮询触发
        // 目的：功能不必自己写轮询代码, 只登记"按下做什么"（Action 或 get/set），
        // 快捷键条目集中建在隐藏段 [Hotkeys]（不占侧栏），由 CheckHotkeys 每帧统一检测（含组合键/序列）。
        private const string HkSec = "Hotkeys";

        private sealed class HkItem {
            public string Id;
            public string Label;
            public ConfigEntry<KeyCode> Key;
            public Action Run;         //按钮类：按下即执行
            public Func<bool> Get;     //开关类：按下即取反
            public Action<bool> Set;
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

        //登记"按下即执行"的按钮动作（Initialize 或页面渲染时调用均可，重复登记无副作用）
        public static void HotkeyAction(string id, string label, Action action) {
            HkItem it = HkEnsure(id, label);
            if (it != null) { it.Run = action; it.Get = null; it.Set = null; }
        }

        //登记"按下即取反"的开关动作：与 HotkeyAction 同走 [Hotkeys] 段 + CheckHotkeys 统一轮询。
        // 补这个入口的意义：HkItem 的 Get/Set 字段原本是**死的**, 只有 HotkeyAction 会赋值，
        //  而它同时把两者置 null，于是 CheckHotkeys 里那条
        //  else if (it.Get != null && it.Set != null) it.Set(!it.Get()); 永不执行。
        //  结果开关类只能另起炉灶（EX 手写了 20+ 行 if (SR.ComboKeyDown(_xxxKey)) Xxx(); 的轮询，
        //  新增一个开关要改两处（Bind + 轮询），漏一处就是"绑了键没反应"）。
        //  有了本入口，开关类也能纳入统一轮询；两套机制可并存，迁移是渐进式的。
        public static void HotkeyToggle(string id, string label, Func<bool> get, Action<bool> set) {
            HkItem it = HkEnsure(id, label);
            if (it != null) { it.Get = get; it.Set = set; it.Run = null; }
        }



        //取该动作的快捷键条目（界面用它做右键绑定与显示）；_internalConfig 就绪后惰性建立
        public static ConfigEntry<KeyCode> Hotkey(string id) {
            HkItem it;
            if (string.IsNullOrEmpty(id) || !_hkById.TryGetValue(id, out it)) return null;
            if (it.Key == null && _internalConfig != null) {
                try {
                    it.Key = _internalConfig.Bind(HkSec, it.Id, KeyCode.None,
                        "自动快捷键：" + (it.Label ?? it.Id) + "（在管理界面右键对应开关/按钮绑定，再右键删除）");
                    RegisterComboEntry(it.Key);
                    NoteEntryBound();   //新增了一个条目：让 SectionEntries 的缓存失效
                    _watchDirty = true;
                } catch (Exception __ex) { Guard.Log("建立自动快捷键条目", __ex); }
            }
            return it.Key;
        }

        //显示文本（"" = 未绑定）；界面把它拼在标签后。
        //录制中同样返回带方括号的紧凑文本（" [...]" / " [1]"）：按下 1 的当帧就显示成 [1]，
        //既不会"先显示 1、松手才变 [1]"，也不会把旧键与新录制内容叠成 "[1]1"；
        //未按键时用单字符省略号（不是"请按键..."），避免固定宽度的标签被挤到下一行。
        public static string HotkeyText(string id) {
            ConfigEntry<KeyCode> k = Hotkey(id);
            if (k == null) return "";
            return KeySuffix(k);
        }

        //键位后缀（"" = 未绑定）：统一给标签/按钮拼文本，录制中显示紧凑的 [...] / [正在录的键]
        public static string KeySuffix(ConfigEntry<KeyCode> k) {
            if (k == null) return "";
            if (_capturing == k) return _recSeq.Count == 0 ? " [...]" : " [" + RecText() + "]";
            KeyCode v = KeyCode.None;
            try { v = k.Value; } catch (Exception __ex) { Guard.Log("KeySuffix", __ex); }
            if (v == KeyCode.None) return "";
            return " [" + ComboKeyDisplay(k, v) + "]";
        }

        //把已登记的条目都建出来（首次按键检查时；此时 _internalConfig 已就绪，旧绑定从 cfg 读回）
        private static void HkInitAll() {
            if (_hkInitDone || _internalConfig == null) return;
            _hkInitDone = true;
            try { NavHide(HkSec); } catch (Exception __ex) { Guard.Log("隐藏 Hotkeys 段", __ex); }
            for (int i = 0; i < _hkItems.Count; i++) {
                // **地基要求**：单个条目建不出来不能连累后面的（_hkInitDone 已置位，抛一次就永久少一批）
                try { Hotkey(_hkItems[i].Id); } catch (Exception __ex) { Guard.Log("建立自动快捷键 " + _hkItems[i].Id, __ex); }
            }
        }

        //每帧轮询（SR.Tick 调用）：绑定的键按下 -> 执行动作 / 切换开关
        public static void CheckHotkeys() {
            // **地基要求**：每个功能的回调单独隔离。原来整个循环共用一个 try/catch,
            //  某个功能抛一次之后，**它后面所有功能的热键每帧都被跳过**（每帧都在同一点抛）。
            try { HkInitAll(); } catch (Exception __ex) { Guard.Log("初始化自动快捷键", __ex); }
            if (_capturing != null) return; //正在录制按键：不触发
            for (int i = 0; i < _hkItems.Count; i++) {
                HkItem it = _hkItems[i];
                try {
                    if (it == null) continue;
                // **兜底**：HkInitAll 只在"内部配置已就绪"的那一帧跑一次；若当时 _internalConfig 还
                //  没准备好（或该 id 是后来才登记进来的），it.Key 会一直是 null -> 这个键永远轮询不到
                //  （表现为"绑了键却没反应"）。这里每帧补一次惰性 Bind，Bind 成功后立刻可用。
                if (it.Key == null) { try { Hotkey(it.Id); } catch (Exception __b) { Guard.Log("惰性建立快捷键 " + it.Id, __b); } }
                if (it.Key == null || it.Key.Value == KeyCode.None) continue;
                    if (!ComboKeyDown(it.Key)) continue;
                    if (it.Run != null) it.Run();
                    else if (it.Get != null && it.Set != null) it.Set(!it.Get());
                } catch (Exception __ex) { Guard.Log("自动快捷键 " + (it != null ? it.Id : "?"), __ex); }
            }
        }

	}
}
