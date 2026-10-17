// 本地化：T() 查表入口、注册表、内置中英文词条、外部语言包，以及键名 / 关卡名 / 枚举名翻译。
using System;
using System.Collections.Generic;
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

        // 悬浮说明的默认值行前缀：4 行文案的最后一行统一加“默认：/Default: ”。
        private static string PrefixDefaultLine(string s, bool en) {
            if (string.IsNullOrEmpty(s)) return s;
            int idx = s.LastIndexOf('\n');
            if (idx < 0) return s;
            string last = s.Substring(idx + 1).Trim();
            if (last.Length == 0) return s;
            string pre = en ? "Default: " : "默认：";
            if (last.StartsWith(en ? "Default" : "默认", StringComparison.Ordinal)) return s;
            return s.Substring(0, idx + 1) + pre + last;
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

        //条目默认值后缀（"默认: X" / "Default: X"），按 entry 缓存；经 T() 生成以便语言包翻译。
        internal static string DefaultSuffix(ConfigEntryBase entry) {
            NameCacheSync();
            string cached;
            if (_defSuffix.TryGetValue(entry, out cached)) return cached;
            string dft = FormatDefaultValue(entry.DefaultValue);
            cached = string.IsNullOrEmpty(dft) ? "" : T("默认: " + dft, "Default: " + dft);
            _defSuffix[entry] = cached;
            return cached;
        }

        //名称列悬浮说明 = 说明本身（4 行文案已含默认值；不再自动追加默认值/恢复提示）。
        internal static string RowTip(ConfigEntryBase entry, string desc) {
            NameCacheSync();
            string cached;
            if (_rowTipCache.TryGetValue(entry, out cached)) return cached;
            string tip = desc != null ? desc : "";
            _rowTipCache[entry] = tip;
            return tip;
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

        //页脚状态：当前游戏模式 · 当前场景（如 自由·农场）。
        private static string ModeLabel() {
            string mode = T("未知", "Unknown");
            try {
                GameState.GameMode gm = GameSettings.GetInstance().GameMode;
                if (gm == GameState.GameMode.FREEPLAY) mode = T("自由", "Freeplay");
                else if (gm == GameState.GameMode.CHALLENGE) mode = T("挑战", "Challenge");
                else if (gm == GameState.GameMode.PARTY) mode = T("派对", "Party");
                else if (gm == GameState.GameMode.CREATIVE) mode = T("创意", "Creative");
                else mode = gm.ToString();
            } catch (Exception __ex) { Guard.Log("ModeLabel", __ex); }
            //场景名查询同样需 try：本文件约定所有 Unity 原生调用一律包裹。
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name; } catch (Exception __ex) { Guard.Log("ModeLabel 场景名", __ex); }
            return mode + "·" + MapNameZh(scene);
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

	}
}
