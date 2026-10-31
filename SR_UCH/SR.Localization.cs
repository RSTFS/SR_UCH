// 本地化：T() 查表入口、注册表、内置中英文词条、外部语言包，以及键名 / 关卡名 / 枚举名翻译。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Localization（T() 入口 / Register 注册表 / 查表回退 / 键名·关卡名·枚举名）
// 分区名、条目名、悬浮说明由各功能文件经 SR.LocSec / SR.LocKey / SR.LocDesc 注册，本文件只保留字典与查表逻辑。

        private static readonly Dictionary<string, string> _sectionZh = new Dictionary<string, string>();

        private static readonly Dictionary<string, string> _keyZh = new Dictionary<string, string>();

        // 界面文案自注册入口（供各功能文件调用）：
        // SR.LocSec(分区, 中文名[, 英文名])；SR.LocKey(分区, 键, 中文名, 英文名)；SR.LocDesc(分区, 键, 中文说明, 英文说明)。
        public static void LocSec(string sec, string zh, string en = null) {
            if (sec == null) return;
            if (zh != null) _sectionZh[sec] = zh;
            if (en != null) _sectionEn[sec] = en;
        }

        public static void LocKey(string sec, string key, string zh, string en) {
            if (sec == null || key == null) return;
            string k = sec + "\t" + key;
            bool changed = false;
            string old;
            if (zh != null && (!_keyZh.TryGetValue(k, out old) || old != zh)) { _keyZh[k] = zh; changed = true; }
            if (en != null && (!_keyEn.TryGetValue(k, out old) || old != en)) { _keyEn[k] = en; changed = true; }
            if (changed) _nameCacheVer = -1; //名字缓存整体失效
        }

        public static void LocDesc(string sec, string key, string zh, string en) {
            if (sec == null || key == null) return;
            string k = sec + "\t" + key;
            bool changed = false;
            string old;
            if (zh != null && (!_descZh.TryGetValue(k, out old) || old != zh)) { _descZh[k] = zh; changed = true; }
            if (en != null && (!_descEn.TryGetValue(k, out old) || old != en)) { _descEn[k] = en; changed = true; }
            if (changed) _nameCacheVer = -1; //说明缓存整体失效
        }

        //登记枚举成员的中文显示名（如 SR.LocEnum("Hold", "按住显示")）；英文模式仍显示枚举原名。
        public static void LocEnum(string name, string zh) {
            if (string.IsNullOrEmpty(name) || zh == null) return;
            _enumZh[name] = zh;
        }

        // 查表回退：中文模式 中文 -> 英文 -> 原名；英文模式 英文 -> 中文 -> 原名；都没有则不显示。
        private static string ZhSection(string sec) {
            string zh, en;
            _sectionZh.TryGetValue(sec, out zh);
            _sectionEn.TryGetValue(sec, out en);
            string ov = Pack(zh, en ?? sec);   //外部语言包覆盖
            if (ov != null) return ov;
            return UseEn ? (en ?? sec) : (zh ?? en ?? sec);
        }

        //英文分区显示名（英文模式使用）。
        private static readonly Dictionary<string, string> _sectionEn = new Dictionary<string, string>();

        //显示名 / 说明文本缓存：语言、条目表版本或 LocKey / LocDesc 注册变化时整体失效。
        //这四个缓存服务于每帧被多次调用的 ZhKey / ZhDesc / 默认值后缀 / 悬浮整串。
        private static readonly Dictionary<ConfigEntryBase, string> _keyNameCache = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, string> _descTextCache = new Dictionary<ConfigEntryBase, string>();
        //条目默认值后缀与名称列悬浮整串的缓存（按 entry，语言不变时结果恒定）。
        private static readonly Dictionary<ConfigEntryBase, string> _defSuffix = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, string> _rowTipCache = new Dictionary<ConfigEntryBase, string>();
        private static int _nameCacheLangVer = -1;   //缓存对应的语言版本
        private static int _nameCacheVer = -1;

        private static bool NameCacheSync() {
            bool en = UseEn;
            if (_nameCacheLangVer != _langVer || _nameCacheVer != _entryVersion) {
                _nameCacheLangVer = _langVer;
                _nameCacheVer = _entryVersion;
                _keyNameCache.Clear();
                _descTextCache.Clear();
                _defSuffix.Clear();
                _rowTipCache.Clear();
            }
            return en;
        }

        private static string ZhKey(ConfigEntryBase e) {
            bool en = NameCacheSync();
            string cached;
            if (_keyNameCache.TryGetValue(e, out cached)) return cached;
            string key = e.Definition.Section + "\t" + e.Definition.Key;
            string zh, enName;
            _keyZh.TryGetValue(key, out zh);
            _keyEn.TryGetValue(key, out enName);
            string ov = Pack(zh, enName ?? e.Definition.Key);   //外部语言包覆盖
            if (ov != null) { _keyNameCache[e] = ov; return ov; }
            string v = en ? (enName ?? e.Definition.Key) : (zh ?? enName ?? e.Definition.Key);
            _keyNameCache[e] = v;
            return v;
        }

        //英文键显示名（英文模式使用）。
        private static readonly Dictionary<string, string> _keyEn = new Dictionary<string, string>();

        //中文悬浮说明（null = 无说明）。
        private static readonly Dictionary<string, string> _descZh = new Dictionary<string, string>();

        //英文悬浮说明，与 _descZh 一一对应。
        private static readonly Dictionary<string, string> _descEn = new Dictionary<string, string>();

        private static string ZhDesc(ConfigEntryBase e) {
            bool en = NameCacheSync();
            string cached;
            if (_descTextCache.TryGetValue(e, out cached)) return cached;
            string key = e.Definition.Section + "\t" + e.Definition.Key;
            string zh, enDesc;
            _descZh.TryGetValue(key, out zh);
            _descEn.TryGetValue(key, out enDesc);
            string ov = Pack(zh, enDesc);   //外部语言包覆盖（zh/enDesc 可能为 null）
            if (ov != null) { _descTextCache[e] = ov; return ov; }
            //"" 表示显式无说明；null 同样缓存（值允许为 null）
            string v = en ? (enDesc ?? zh ?? null) : (zh ?? enDesc ?? null);
            _descTextCache[e] = v;
            return v;
        }

        //条目默认值后缀（"默认：X" / "Default: X"），按 entry 缓存；经 T() 生成以便语言包翻译。
        //无默认值（含 KeyCode.None / 空字符串）时返回 ""，调用方不追加该行。
        internal static string DefaultSuffix(ConfigEntryBase entry) {
            NameCacheSync();
            string cached;
            if (_defSuffix.TryGetValue(entry, out cached)) return cached;
            string dft = FormatDefaultValue(entry);
            cached = string.IsNullOrEmpty(dft) ? "" : T("默认：" + dft, "Default: " + dft);
            _defSuffix[entry] = cached;
            return cached;
        }


        //名称列悬浮说明 = 说明本身（固定 3 行）+ 核心追加的第 4 行（默认值 / 按键）。
        //说明文案不再自带默认值；无默认值时只显示 3 行。
        internal static string RowTip(ConfigEntryBase entry, string desc) {
            NameCacheSync();
            string cached;
            if (_rowTipCache.TryGetValue(entry, out cached)) return cached;
            string tip = ComposeTip(entry, desc, null);
            _rowTipCache[entry] = tip;
            return tip;
        }

        // 悬浮条目统一组装（用户要求：是什么 → 做什么 → 注意事项 → 默认值 → 按键）：
        //   desc 是本模组登记的 3 行说明（LocDesc），默认值与按键由核心按 entry 自动补，
        //   所以 174 条说明不需要各写一遍默认值，顺序也永远一致。
        internal static string ComposeTip(ConfigEntryBase entry, string desc, ConfigEntry<KeyCode> hk) {
            StringBuilder sb = new StringBuilder(160);
            string body = NormalizeTip(desc);
            if (body.Length > 0) sb.Append(body);
            if (entry != null) {
                string dft = DefaultSuffix(entry);
                if (!string.IsNullOrEmpty(dft)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(dft); }
            }
            if (hk != null) { if (sb.Length > 0) sb.Append('\n'); sb.Append(KeyTipLine(hk)); }
            return sb.ToString();
        }

        // 去掉首尾空行、折叠连续空行（修「第一行空行却换行显示」：说明里带前导 \n 时不再留白）。
        internal static string NormalizeTip(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string> keep = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++) {
                string l = lines[i] != null ? lines[i].TrimEnd() : "";
                bool blank = l.Trim().Length == 0;
                if (blank && (keep.Count == 0 || keep[keep.Count - 1].Trim().Length == 0)) continue;  // 首行/连续空行丢弃
                keep.Add(l);
            }
            while (keep.Count > 0 && keep[keep.Count - 1].Trim().Length == 0) keep.RemoveAt(keep.Count - 1);
            return string.Join("\n", keep.ToArray());
        }

        // 按键行（第 5 行）：未绑定时给出"右键设置/删除"的指引，绑定了就显示实际按键。
        internal static string KeyTipLine(ConfigEntry<KeyCode> hk) {
            if (hk == null) return "";
            KeyCode v = KeyCode.None;
            try { v = hk.Value; } catch (Exception __ex) { Guard.Log("按键提示行", __ex); return ""; }
            if (v == KeyCode.None) return T("按键：右键设置/删除快捷键", "Key: right-click to set/remove");
            return T("按键：" + ComboKeyDisplay(hk, v), "Key: " + ComboKeyDisplay(hk, v));
        }

        //键位显示名：按语言返回中文名或 KeyCode 原名。
        public static string KeyDisplayName(KeyCode k) {
            string en = k.ToString();
            string zh = KeyZh(k);
            string ov = Pack(zh, en);   //外部语言包覆盖
            if (ov != null) return ov;
            //键位一律显示原始 KeyCode 名（本身就是英文）
            return en;   // 一律显示原始 KeyCode 名（Delete / Backspace / LeftAlt），不做中文翻译
        }

        //键位中文名，无专门译名时返回 null。
        private static string KeyZh(KeyCode k) {
            switch (k) {
                case KeyCode.None: return "未设置";
                case KeyCode.LeftAlt: return "左Alt";
                case KeyCode.RightAlt: return "右Alt";
                case KeyCode.LeftControl: return "左Ctrl";
                case KeyCode.RightControl: return "右Ctrl";
                case KeyCode.LeftShift: return "左Shift";
                case KeyCode.RightShift: return "右Shift";
                case KeyCode.Return: return "回车";
                case KeyCode.Escape: return "Esc";
                case KeyCode.Backspace: return "退格";
                case KeyCode.Delete: return "删除";
                case KeyCode.Space: return "空格";
                case KeyCode.UpArrow: return "上方向";
                case KeyCode.DownArrow: return "下方向";
                case KeyCode.LeftArrow: return "左方向";
                case KeyCode.RightArrow: return "右方向";
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                case KeyCode.Mouse3: return "鼠标键4";
                case KeyCode.Mouse4: return "鼠标侧键1";
                case KeyCode.Mouse5: return "鼠标侧键2";
                case KeyCode.Mouse6: return "鼠标键7";
                default:
                    //F1-F12 / 数字键无中文译名，返回 null
                    return null;
            }
        }

        public static string MapNameZh(string scene) {
            string zh = MapZh(scene);
            string ov = Pack(zh, scene);   //外部语言包覆盖
            if (ov != null) return ov;
            //英文模式：显示场景原名（本身就是英文）
            return UseEn ? scene : (zh ?? scene);
        }

        //场景名（如 "Farm"）转中文关卡名：查 _enumZh（大写匹配），其次树屋 / 大厅。
        private static string MapZh(string scene) {
            if (string.IsNullOrEmpty(scene)) return null;
            string zh;
            if (_enumZh.TryGetValue(scene.ToUpperInvariant(), out zh)) return zh;
            if (scene == "Treehouse") return "树屋";
            if (scene == "Lobby") return "大厅";
            return null;
        }

        //页脚状态：当前游戏模式 · 当前关卡（模式/关卡名都跟随游戏本地化语言）。
        // 每帧都算一次太重：I2 查表 + Enum 全表遍历 + 字符串拼接（audit E-9）。
        // 这里按 (场景, 模式, 快照名, 语言版本) 做记忆化——底栏每帧调它也只是一次字符串比较。
        private static string _modeLabelKey;
        private static string _modeLabelVal;
        private static string ModeLabel() {
            string key = null;
            try {
                string sc = "";
                try { sc = SceneManager.GetActiveScene().name; } catch (Exception __sc) { Guard.Log("ModeLabel 场景名(缓存键)", __sc); }
                string snap = "";
                try {
                    LevelSelectController.PlayedSnapshotInfo info = GameState.GetInstance().currentSnapshotInfo;
                    // PlayedSnapshotInfo 是 struct：没有 null 可判，直接看字段。
                    if (!string.IsNullOrEmpty(info.snapshotName)) snap = info.snapshotName;
                } catch (Exception __si) { Guard.Log("ModeLabel 快照名(缓存键)", __si); }
                // 模式读不到（刚启动 / 切场景瞬间 GetInstance() 会抛）时不要把假值写进缓存键，
                // 否则缓存每帧不命中、页脚模式文本反复闪烁且每次都全量重算（audit 08-P0-04）。
                GameState.GameMode gmKey = GameState.GameMode.CREATIVE;
                bool __gmOk = false;
                try { gmKey = GameSettings.GetInstance().GameMode; __gmOk = true; }
                catch (Exception __gk) { Guard.Log("ModeLabel 模式(缓存键)", __gk); }
                if (!__gmOk) { key = null; return ModeLabelRaw(); }
                key = sc + "|" + (int)gmKey + "|" + snap + "|" + LangVer + "|" + (_forceZh ? 1 : 0) + "|" + (UseEn ? 1 : 0);
                if (key == _modeLabelKey && _modeLabelVal != null) return _modeLabelVal;
            } catch { LogWarn("ModeLabel 缓存键失败，退回直算"); }
            string computed = ModeLabelRaw();
            if (!string.IsNullOrEmpty(key)) { _modeLabelKey = key; _modeLabelVal = computed; }
            return computed;
        }

        private static string ModeLabelRaw() {
            string mode = T("未知", "Unknown");
            try {
                GameState.GameMode gm = GameSettings.GetInstance().GameMode;
                string term = null;
                if (gm == GameState.GameMode.FREEPLAY) term = "RuleBook/FreePlayText";
                else if (gm == GameState.GameMode.CHALLENGE) term = "RuleBook/ChallengeModeText";
                else if (gm == GameState.GameMode.PARTY) term = "RuleBook/PartyModeText";
                else if (gm == GameState.GameMode.CREATIVE) term = "RuleBook/CreativeModeText";
                bool got = false;
                if (!string.IsNullOrEmpty(term)) {
                    try {
                        string loc = I2.Loc.LocalizationManager.GetTranslation(term);
                        if (!string.IsNullOrEmpty(loc) && loc.IndexOf('/') < 0) { mode = loc; got = true; }
                    } catch (Exception __gx) { Guard.Log("ModeLabel 模式本地化", __gx); }
                }
                if (!got) mode = gm.ToString();
            } catch (Exception __ex) { Guard.Log("ModeLabel", __ex); }
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name; } catch (Exception __ex) { Guard.Log("ModeLabel 场景名", __ex); }
            string level = null;
            try {
                if (scene == "Treehouse") level = T("树屋", "Treehouse");
                else if (scene == "Lobby") level = T("大厅", "Lobby");
                else if (scene == "BlankLevel") {
                    LevelSelectController.PlayedSnapshotInfo info = GameState.GetInstance().currentSnapshotInfo;
                    level = !string.IsNullOrEmpty(info.snapshotName) ? info.snapshotName : T("自定义关卡", "Custom level");
                } else if (!string.IsNullOrEmpty(scene)) {
                    foreach (GameState.LevelName lv in Enum.GetValues(typeof(GameState.LevelName))) {
                        if (GameState.GetLevelSceneName(lv) == scene) { level = GameState.GetLevelLocalizedName(lv); break; }
                    }
                }
            } catch (Exception __lx) { Guard.Log("ModeLabel 关卡本地化", __lx); }
            if (string.IsNullOrEmpty(level)) level = MapNameZh(scene);
            return mode + "·" + level;
        }

        //界面语言开关（设置页配置，运行时立即生效）；外部模块页渲染时 _forceZh=true 强制中文。
        private static bool _langEn = true; //默认英文
        private static bool _forceZh = false;
        private static ConfigEntry<string> _langEntry;

        // 语言标识：内置中文 / English，外部语言包使用自己的 Id（public 供独立程序集判断当前语言）。
        // 所有文案出口先查语言包词条表，未收录的词条按 FallbackId 回退内置语言。
        public const string LangZh = "中文";
        
        public const string LangEn = "English";
        private static readonly Dictionary<string, ILanguagePack> _packs = new Dictionary<string, ILanguagePack>();
        private static ILanguagePack _pack;      //当前生效的外部语言包（内置中/英时为 null）
        private static bool _fallbackEn;         //外部语言未收录时回退英文（否则回退中文）
        private static string _langId = LangEn;
        private static int _langVer = 0;         //语言版本号：变化时显示名缓存整体失效

        //下拉框的值 / 显示名两列：内置两门在前，语言包按注册顺序跟在后面
        public static void LanguageOptions(out string[] ids, out string[] names) {
            List<string> lIds = new List<string>();
            List<string> lNames = new List<string>();
            lIds.Add(LangZh); lNames.Add(LangZh);
            lIds.Add(LangEn); lNames.Add(LangEn);
            foreach (KeyValuePair<string, ILanguagePack> kv in _packs) {
                string nm = null;
                try { nm = kv.Value.DisplayName; } catch (Exception __ex) { Guard.Log("LanguageOptions", __ex); }
                lIds.Add(kv.Key);
                lNames.Add(string.IsNullOrEmpty(nm) ? kv.Key : nm);
            }
            ids = lIds.ToArray();
            names = lNames.ToArray();
        }

        //注册外部语言包（MainPlugin 扫描 plugins 目录时调用，插件亦可自行调用）。
        //id 重复则覆盖；与内置语言重名则忽略。
        public static void RegisterLanguage(ILanguagePack pack) {
            if (pack == null) return;
            string id;
            try { id = pack.Id; } catch { return; }
            if (string.IsNullOrEmpty(id)) return;
            if (id == LangZh || id == LangEn) {
                if (MainPlugin.ModLogger != null) SR.LogWarn("[SR] 语言包 id 与内置语言冲突，已忽略: " + id);
                return;
            }
            _packs[id] = pack;
            ApplyLanguage();   //cfg 里可能已经选了这门语言（上次装过 / 注册晚于 Bind）
            string nm = id;
            try { if (!string.IsNullOrEmpty(pack.DisplayName)) nm = pack.DisplayName; } catch (Exception __ex) { Guard.Log("RegisterLanguage", __ex); }
            if (MainPlugin.ModLogger != null) SR.LogInfo("[SR] 已注册语言包: " + nm + " (" + id + ")");
        }

        //按 cfg 中的语言 id 重新解析当前语言（启动 / 切语言 / 注册语言包都走这里）。
        //语言包被删除后 cfg 仍留其 id，此时回退内置英文。
        public static void ApplyLanguage() {
            string id = null;
            try { if (_langEntry != null) id = _langEntry.Value; } catch (Exception __ex) { Guard.Log("ApplyLanguage", __ex); }
            if (string.IsNullOrEmpty(id)) id = LangEn;
            ILanguagePack p;
            if (_packs.TryGetValue(id, out p)) {
                _pack = p;
                _langId = id;
                _langEn = false;                 //非内置英文
                bool fbZh = false;
                try { fbZh = string.Equals(p.FallbackId, LangZh, StringComparison.Ordinal); } catch (Exception __ex) { Guard.Log("ApplyLanguage", __ex); }
                _fallbackEn = !fbZh;
            } else {
                _pack = null;
                if (string.Equals(id, LangZh, StringComparison.Ordinal)) {
                    _langId = LangZh; _langEn = false; _fallbackEn = false;
                } else {
                    _langId = LangEn; _langEn = true; _fallbackEn = true;
                }
            }
            _langVer++;
        }

        //当前语言 id（内置 "中文" / "English"，或语言包 Id），供功能做按语言的缓存失效。
        public static string CurrentLanguageId { get { return _langId; } }

        //语言版本号：切语言或注册语言包时 +1。按语言缓存测量结果（列宽 / 文本高度）处须比较本值。
        public static int LangVer { get { return _langVer; } }

        //当前是否按英文输出：内置英文或语言包声明回退英文；外部模块页强制中文时为 false。
        private static bool UseEn {
            get { return (_langEn || _fallbackEn) && !_forceZh; }
        }

        //外部语言包覆盖：按中文原文查，其次按英文原名查；外部模块页（_forceZh）不查。
        //词条表引用按语言版本号缓存，避免语言包把 Map 写成计算属性时反复重建整张表。
        private static IDictionary<string, string> _packMap;
        private static int _packMapVer = -1;

        private static string Pack(string zh, string en) {
            if (_pack == null || _forceZh) return null;
            if (_packMapVer != _langVer) {
                _packMapVer = _langVer;
                try { _packMap = _pack.Map; } catch { _packMap = null; }
            }
            IDictionary<string, string> map = _packMap;
            if (map == null) return null;
            //T() 每帧被调用上百次，语言包取词异常不得冒到 UI，取不到即整体回退内置语言。
            try {
                string ov;
                if (zh != null && map.TryGetValue(zh, out ov) && ov != null) return ov;
                if (en != null && map.TryGetValue(en, out ov) && ov != null) return ov;
            } catch (Exception __ex) { Guard.Log("Pack", __ex); }
            return null;
        }

        public static string T(string zh, string en) {
            string ov = Pack(zh, en);
            if (ov != null) return ov;
            return UseEn ? (en ?? zh) : zh;
        }

        private static readonly Dictionary<string, string> _enumZh = new Dictionary<string, string> {
            { "PUBLIC", "公开" }, { "FRIENDS", "仅限朋友" }, { "PRIVATE", "仅邀请" }, { "INVISIBLE", "隐身" },
            { "Fun", "好玩" }, { "Competitive", "竞技" }, { "Beginner", "新手" }, { "CustomLevels", "自定义关卡" },
            { "win", "获胜" }, { "winDead", "死后" }, { "soloWin", "独行" }, { "first", "第一" },
            { "trap", "陷阱" }, { "suicide", "自杀" }, { "comeback", "逆转" }, { "coin", "金币" },
            { "second", "第二" }, { "third", "第三" }, { "fourth", "第四" },
            { "NONE", "无" }, { "None", "无" }, { "Shift", "Shift" }, { "Ctrl", "Ctrl" }, { "Alt", "Alt" },
            { "CHICKEN", "鸡" }, { "HORSE", "马" }, { "SHEEP", "羊" }, { "RACCOON", "浣熊" },
            { "Placement", "放置顺序" }, { "Distance", "距离" }, { "Normal", "普通" }, { "Advanced", "进阶" },
            { "NoTrack", "不追踪" }, { "P1", "#1" }, { "P2", "#2" }, { "P3", "#3" }, { "P4", "#4" },
            { "KeepScore", "保留方块和分数" }, { "KeepBlocksOnly", "仅保留方块" },
            { "CHAMELEON", "变色龙" }, { "SQUIRREL", "松鼠" }, { "ROBOT", "机器兔子" }, { "ELEPHANT", "大象" },
            { "MONKEY", "猴子" }, { "SNAKE", "蛇" }, { "HIPPO", "河马" }, { "TURTLE", "乌龟" },
            { "PANDA", "熊猫" }, { "FOX", "狐狸" }, { "PLATYPUS", "鸭嘴兽" },
            { "FARM", "农场" }, { "ROOFTOPS", "屋顶" }, { "OLDMANSION", "旧房子" }, { "WATERFALL", "瀑布" },
            { "PYRAMID", "金字塔" }, { "WINDMILL", "风车" }, { "METALPLANT", "冶炼厂" }, { "ICEBERG", "冰山" },
            { "DANCEPARTY", "舞会" }, { "PIER", "码头" }, { "BLANKLEVEL", "空白" }, { "JUNGLETEMPLE", "丛林神庙" },
            { "VOLCANO", "火山" }, { "CRUMBLINGBRIDGE", "碎碎桥" }, { "NUCLEARPLANT", "核工厂" }, { "TRONLEVEL", "主机架" },
            { "SPACELEVEL", "太空" }, { "BALLROOM", "宴会厅" }, { "ROLLERCOASTER", "过山车" }, { "METRO", "地铁站" },
            { "WATERTOWER", "毒塔" }, { "RAFT", "岛屿" }, { "PICTUREFRAME", "画框" }, { "SPACESTATION", "空间站" },
            { "RANDOM", "随机" },
            { "MAINMENU", "主菜单" }, { "TREEHOUSE", "树屋" }, { "LOBBY", "大厅" }, { "TUTORIAL", "教程" },
            { "Wins", "胜场" }, { "Success", "成功" }, { "Deaths", "死亡" }, { "Coins", "金币" },
            { "LevelsPlayed", "关卡数" }, { "Rounds", "回合数" }, { "TimePlayed", "总时间" },
        };

        //枚举显示名：转发 EnumNameZh。
        private static string EnumDisplayName(string n) {
            return EnumNameZh(n);
        }

        //枚举中文名（跨 DLL 使用）：中文模式查 _enumZh，英文模式返回原名。
        public static string EnumNameZh(string n) {
            string zh;
            _enumZh.TryGetValue(n, out zh);
            string ov = Pack(zh, n);   //外部语言包覆盖
            if (ov != null) return ov;
            if (UseEn) return n; //英文系直接显示原名
            return zh ?? n;
        }

        // ---------- 外置 JSON 语言包（config/SR_UCH/lang/*.json） ----------
        // 第三方语言包不再必须编译 DLL：把 {"id","displayName","fallback","map"} 写进 JSON 即可。
        // 加载点在 SR.Initialize 早期（语言条目 Bind + ApplyLanguage 之后、各功能 Initialize 之前），
        // 失败只写日志并跳过对应文件，绝不抛出（语言包缺失不能影响核心启动）。

        private const long MaxLangPackFileBytes = 2L * 1024L * 1024L; // 单文件 2MB 上限
        private const int MaxLangPackEntries = 20000;                // map 最多 2 万条

        // JSON 语言包实现：Map 直接返回解析出的字典；ApplyLanguage / LanguageOptions 走现有逻辑，
        // 因此设置页语言下拉会自动多出这一项，无需改 UI。
        private sealed class JsonLanguagePack : ILanguagePack {
            private readonly string _id;
            private readonly string _displayName;
            private readonly string _fallbackId;
            private readonly Dictionary<string, string> _map;

            public JsonLanguagePack(string id, string displayName, string fallbackId, Dictionary<string, string> map) {
                _id = id;
                _displayName = displayName;
                _fallbackId = fallbackId;
                _map = map;
            }

            public string Id { get { return _id; } }
            public string DisplayName { get { return _displayName; } }
            public string FallbackId { get { return _fallbackId; } }
            public IDictionary<string, string> Map { get { return _map; } }
        }

        // 扫描并注册 config/SR_UCH/lang/*.json。目录不存在 = 正常情况（未安装任何 JSON 语言包）。
        internal static void LoadJsonLanguagePacks() {
            try {
                string dir;
                try { dir = Path.Combine(Paths.ConfigPath, "SR_UCH", "lang"); }
                catch (Exception __ex) { Guard.Log("语言包/定位目录", __ex); return; }
                try { if (!Directory.Exists(dir)) return; }
                catch (Exception __ex) { Guard.Log("语言包/检查目录", __ex); return; }
                string[] files;
                try { files = Directory.GetFiles(dir, "*.json"); }
                catch (Exception __ex) { Guard.Log("语言包/枚举文件", __ex); return; }
                // 按文件名排序后加载：同 id 时后加载覆盖（与 RegisterLanguage 的覆盖语义一致），顺序可预期。
                try { Array.Sort(files, StringComparer.OrdinalIgnoreCase); }
                catch (Exception __ex) { Guard.Log("语言包/排序文件", __ex); }
                int loaded = 0;
                int skipped = 0;
                // 已加载的 id -> 来源文件名：同名 id 后加载覆盖，并记一条日志方便排查。
                Dictionary<string, string> seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < files.Length; i++) {
                    if (LoadOneJsonLanguagePack(files[i], seenIds)) loaded++;
                    else skipped++;
                }
                if (loaded > 0) {
                    SR.LogInfo("[SR] 已从 " + dir + " 加载 " + loaded + " 个 JSON 语言包"
                        + (skipped > 0 ? "（跳过 " + skipped + " 个无效文件）" : ""));
                } else if (skipped > 0) {
                    SR.LogWarn("[SR] " + dir + " 下的 " + skipped + " 个 JSON 语言包全部无效，已跳过");
                }
            } catch (Exception __ex) {
                Guard.Log("语言包/批量加载", __ex);
            }
        }

        // 加载单个 JSON 语言包；返回 false 表示已记录日志并跳过（每个文件最多一条告警）。
        private static bool LoadOneJsonLanguagePack(string path, Dictionary<string, string> seenIds) {
            string fileName = "<unknown>";
            try {
                fileName = Path.GetFileName(path);
                long length;
                try { length = new FileInfo(path).Length; }
                catch (Exception __ex) { Guard.Log("语言包/读取文件大小 " + fileName, __ex); return false; }
                if (length <= 0) {
                    SR.LogWarn("[SR] 语言包文件为空，已跳过: " + fileName);
                    return false;
                }
                if (length > MaxLangPackFileBytes) {
                    SR.LogWarn("[SR] 语言包文件超过 2MB 上限，已跳过: " + fileName + "（" + length + " 字节）");
                    return false;
                }
                string text;
                try { text = File.ReadAllText(path, Encoding.UTF8); } // 兼容 BOM（StreamReader 自动识别）
                catch (Exception __ex) { Guard.Log("语言包/读取文件 " + fileName, __ex); return false; }
                string id;
                string displayName;
                string fallbackId;
                string err;
                Dictionary<string, string> map;
                if (!JsonLanguagePackParser.TryParse(text, out id, out displayName, out fallbackId, out map, out err)) {
                    SR.LogWarn("[SR] JSON 语言包无效，已跳过: " + fileName + "（" + err + "）");
                    return false;
                }
                // 与内置语言 id 冲突：RegisterLanguage 也会忽略，这里提前跳过并给出更明确的一条日志。
                if (id == LangZh || id == LangEn) {
                    SR.LogWarn("[SR] JSON 语言包 id 与内置语言冲突，已跳过: " + fileName + "（id=" + id + "）");
                    return false;
                }
                if (seenIds != null) {
                    string prev;
                    if (seenIds.TryGetValue(id, out prev)) {
                        SR.LogWarn("[SR] JSON 语言包 id 重复，后加载覆盖: " + id + "（" + fileName + " 覆盖 " + prev + "）");
                    }
                    seenIds[id] = fileName;
                }
                if (string.IsNullOrEmpty(displayName)) displayName = id;
                if (string.IsNullOrEmpty(fallbackId)) fallbackId = LangEn;
                RegisterLanguage(new JsonLanguagePack(id, displayName, fallbackId, map));
                SR.LogInfo("[SR] 已加载 JSON 语言包: " + displayName + " (" + id + "，" + map.Count + " 条) <- " + fileName);
                return true;
            } catch (Exception __ex) {
                Guard.Log("语言包/加载 " + fileName, __ex);
                return false;
            }
        }

        // 最小 JSON 解析器：只取顶层对象里的 id / displayName / fallback（字符串）与 map（对象 string->string）。
        // 其余字段、map 中的非字符串值、以及数组/数字/布尔/null 一律只做语法跳过，不构造对象。
        private sealed class JsonLanguagePackParser {
            private string _s;
            private int _i;

            public static bool TryParse(string text, out string id, out string displayName, out string fallbackId,
                out Dictionary<string, string> map, out string err) {
                id = null;
                displayName = null;
                fallbackId = null;
                map = null;
                err = null;
                JsonLanguagePackParser p = new JsonLanguagePackParser();
                p._s = text ?? "";
                p._i = 0;
                try {
                    p.SkipWs();
                    if (!p.TryConsume('{')) return p.Fail("根节点必须是对象", out err);
                    Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.Ordinal);
                    bool hasMap = false;
                    p.SkipWs();
                    if (!p.TryConsume('}')) {
                        while (true) {
                            p.SkipWs();
                            string key;
                            if (!p.TryReadString(out key)) return p.Fail("对象键必须是字符串", out err);
                            p.SkipWs();
                            if (!p.TryConsume(':')) return p.Fail("字段 " + key + " 后缺少冒号", out err);
                            p.SkipWs();
                            if (key == "id" || key == "displayName" || key == "fallback") {
                                string v;
                                if (!p.TryReadString(out v)) {
                                    if (!p.SkipValue()) return p.Fail("字段 " + key + " 的值语法错误", out err);
                                    return p.Fail("字段 " + key + " 必须是字符串", out err);
                                }
                                if (key == "id") id = v;
                                else if (key == "displayName") displayName = v;
                                else fallbackId = v;
                            } else if (key == "map") {
                                if (!p.TryReadStringMap(out m, out err)) return false;
                                hasMap = true;
                            } else {
                                if (!p.SkipValue()) return p.Fail("字段 " + key + " 的值语法错误", out err);
                            }
                            p.SkipWs();
                            if (p.TryConsume(',')) continue;
                            if (p.TryConsume('}')) break;
                            return p.Fail("对象成员之间缺少逗号", out err);
                        }
                    }
                    p.SkipWs();
                    if (p._i != p._s.Length) return p.Fail("根对象之后有多余内容", out err);
                    if (string.IsNullOrEmpty(id)) return p.Fail("缺少 id 字段或 id 为空", out err);
                    if (!hasMap || m.Count == 0) return p.Fail("缺少 map 字段或 map 为空", out err);
                    map = m;
                    return true;
                } catch (Exception __ex) {
                    err = __ex.GetType().Name;
                    return false;
                }
            }

            private bool Fail(string msg, out string err) { err = msg; return false; }

            private void SkipWs() {
                while (_i < _s.Length) {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _i++; continue; }
                    break;
                }
            }

            private bool TryConsume(char c) {
                if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
                return false;
            }

            // JSON 字符串：支持 \" \\ \/ \b \f \n \r \t \uXXXX 转义；\u 逐码元追加（代理对由两个转义自然拼成）。
            private bool TryReadString(out string value) {
                value = null;
                SkipWs();
                if (!TryConsume('"')) return false;
                StringBuilder sb = new StringBuilder();
                while (_i < _s.Length) {
                    char c = _s[_i++];
                    if (c == '"') { value = sb.ToString(); return true; }
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) return false;
                    char e = _s[_i++];
                    switch (e) {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) return false;
                            int cp;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp)) return false;
                            sb.Append((char)cp);
                            _i += 4;
                            break;
                        default: return false;
                    }
                }
                return false;
            }

            // map：必须是对象；键必须是字符串，值只收字符串，其他类型按规格跳过（不整文件作废）。
            private bool TryReadStringMap(out Dictionary<string, string> map, out string err) {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                err = null;
                SkipWs();
                if (!TryConsume('{')) { err = "map 必须是对象"; return false; }
                SkipWs();
                if (TryConsume('}')) return true;
                while (true) {
                    SkipWs();
                    string key;
                    if (!TryReadString(out key)) { err = "map 的键必须是字符串"; return false; }
                    SkipWs();
                    if (!TryConsume(':')) { err = "map 成员 " + key + " 后缺少冒号"; return false; }
                    SkipWs();
                    string val;
                    if (TryReadString(out val)) {
                        if (map.Count >= MaxLangPackEntries) { err = "map 超过 " + MaxLangPackEntries + " 条上限"; return false; }
                        map[key] = val; // 同键重复：后者覆盖
                    } else {
                        if (!SkipValue()) { err = "map 成员 " + key + " 的值语法错误"; return false; }
                    }
                    SkipWs();
                    if (TryConsume(',')) continue;
                    if (TryConsume('}')) return true;
                    err = "map 成员之间缺少逗号";
                    return false;
                }
            }

            // 语法级跳过任意 JSON 值（字符串 / 对象 / 数组 / 数字 / true / false / null）。
            private bool SkipValue() {
                SkipWs();
                if (_i >= _s.Length) return false;
                char c = _s[_i];
                if (c == '"') {
                    string tmp;
                    return TryReadString(out tmp);
                }
                if (c == '{') {
                    _i++;
                    SkipWs();
                    if (TryConsume('}')) return true;
                    while (true) {
                        SkipWs();
                        string k;
                        if (!TryReadString(out k)) return false;
                        SkipWs();
                        if (!TryConsume(':')) return false;
                        if (!SkipValue()) return false;
                        SkipWs();
                        if (TryConsume(',')) continue;
                        return TryConsume('}');
                    }
                }
                if (c == '[') {
                    _i++;
                    SkipWs();
                    if (TryConsume(']')) return true;
                    while (true) {
                        if (!SkipValue()) return false;
                        SkipWs();
                        if (TryConsume(',')) continue;
                        return TryConsume(']');
                    }
                }
                // 数字 / true / false / null：吃到分隔符为止；不做数值校验（跳过即可）。
                int start = _i;
                while (_i < _s.Length) {
                    char d = _s[_i];
                    if (d == ',' || d == '}' || d == ']' || d == ' ' || d == '\t' || d == '\r' || d == '\n') break;
                    _i++;
                }
                return _i > start;
            }
        }

    }
}
