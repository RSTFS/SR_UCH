// 本地化。T() 查表入口、注册表、内置中英文词条，还有外部语言包（一个 DLL 就是一门语言）。
// 键名 / 关卡名 / 枚举名的翻译也在这儿。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Localization（中英文界面：T() 入口 / Register 注册表 / 查表 fallback / 键名·关卡名·枚举名）
// 分区名/条目名/悬浮说明不再集中在本文件, 每个功能在自己的 cs 里用
//      SR.LocSec / SR.LocKey / SR.LocDesc 注册（Bind 与文案写在同一文件）；本文件只留字典与查表逻辑。

        private static readonly Dictionary<string, string> _sectionZh = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        private static readonly Dictionary<string, string> _keyZh = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        // T6：界面文案自注册入口
        //原来所有分区名/键名/悬浮说明都堆在本文件的 5 张表里；现在每个功能的文案都放在
        //功能自己的 cs 里（Bind 与文案同文件），本文件只剩字典 + 这三个 Register + 查表 fallback：
        //    SR.LocSec("Camera", "视野");                                // 分区显示名（en 省略 = 英文原名）
        //    SR.LocKey("Camera", "Free Camera", "自由相机", null);        // 键显示名（null = 用原名）
        //    SR.LocDesc("Camera", "Free Camera", "中文说明", "English");   // 悬浮说明（"" = 空说明）
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
            if (changed) _nameCacheVer = -1; //显示名表变了 -> 让 ZhKey 的名字缓存整体失效（注册集中在启动期）
        }

        public static void LocDesc(string sec, string key, string zh, string en) {
            if (sec == null || key == null) return;
            string k = sec + "\t" + key;
            bool changed = false;
            string old;
            if (zh != null && (!_descZh.TryGetValue(k, out old) || old != zh)) { _descZh[k] = zh; changed = true; }
            if (en != null && (!_descEn.TryGetValue(k, out old) || old != en)) { _descEn[k] = en; changed = true; }
            if (changed) _nameCacheVer = -1; //说明表变了 -> 让 ZhDesc 的说明缓存整体失效
        }

        //枚举成员的显示名（各功能自己声明，如 SR.LocEnum("Hold", "按住显示")）：
        //中文模式用它，英文模式仍显示枚举原名（Hold/Toggle 本身就是英文）。
        public static void LocEnum(string name, string zh) {
            if (string.IsNullOrEmpty(name) || zh == null) return;
            _enumZh[name] = zh;
        }

        //  中文模式：中文 -> 英文覆盖 -> key 原名；英文模式：英文覆盖 -> key 原名。
        //  悬浮说明：中文模式 中文 -> 英文；英文模式 英文 -> 中文；都没有 = null（不显示）。
        private static string ZhSection(string sec) {
            string zh, en;
            _sectionZh.TryGetValue(sec, out zh);
            _sectionEn.TryGetValue(sec, out en);
            string ov = Pack(zh, en ?? sec);   //外部语言：按中文分区名 / 英文原名覆盖
            if (ov != null) return ov;
            return UseEn ? (en ?? sec) : (zh ?? en ?? sec);
        }

        //英文显示覆盖（英文模式侧边栏/分区标题用；T6 起由各功能文件 LocSec 注册）
        private static readonly Dictionary<string, string> _sectionEn = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        //显示名/说明文本缓存：ZhKey/ZhDesc 原来每次调用都要 Section+"\t"+Key 拼字符串 + 两次字典查表，
        //而它们每帧被调很多次（条目行名、列宽测量、联机列表...），IMGUI 一帧还有 Layout+Repaint 两趟。
        //失效条件照项目已有写法（见 SectionEntries/Experiments 统计缓存）：
        //  · 语言变化（中文/英文、外部模块页 ForceZh）；
        //  · 条目表版本 _entryVersion 变化（新增/改名的惰性绑定条目）；
        //  · LocKey/LocDesc 注册了新名（启动期注册，之后不可能再变）。
        private static readonly Dictionary<ConfigEntryBase, string> _keyNameCache = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, string> _descTextCache = new Dictionary<ConfigEntryBase, string>();
        //条目默认值后缀（"默认: X" / "Default: X"）与名称列悬浮整串：
        //entry 的默认值恒定、只有语言会变 -> 按 entry 缓存，未命中才真正拼字符串。
        //（原来每条目每帧都要 FormatDefaultValue + T(zh,en) 两个实参各拼一遍）
        private static readonly Dictionary<ConfigEntryBase, string> _defSuffix = new Dictionary<ConfigEntryBase, string>();
        private static readonly Dictionary<ConfigEntryBase, string> _rowTipCache = new Dictionary<ConfigEntryBase, string>();
        private static int _nameCacheLangVer = -1;   //缓存生成时的语言版本（切语言 / 装语言包都失效）
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
            //外部语言：按中文条目名 / 英文原名覆盖（与 ZhSection / ZhDesc / T 等出口同一套，见 Pack）
            string ov = Pack(zh, enName ?? e.Definition.Key);
            if (ov != null) { _keyNameCache[e] = ov; return ov; }
            string v = en ? (enName ?? e.Definition.Key) : (zh ?? enName ?? e.Definition.Key);
            _keyNameCache[e] = v;
            return v;
        }

        //英文 key 显示覆盖（英文模式用；T6 起由各功能文件 LocKey 注册）
        private static readonly Dictionary<string, string> _keyEn = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        //悬浮说明（中文 tooltip；T6 起由各功能文件 LocDesc 注册，null = 无说明）
        private static readonly Dictionary<string, string> _descZh = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        //悬浮说明（英文 tooltip；与 _descZh 条目一一对应，英文模式查这张表）
        private static readonly Dictionary<string, string> _descEn = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        private static string ZhDesc(ConfigEntryBase e) {
            bool en = NameCacheSync();
            string cached;
            if (_descTextCache.TryGetValue(e, out cached)) return cached;
            string key = e.Definition.Section + "\t" + e.Definition.Key;
            string zh, enDesc;
            _descZh.TryGetValue(key, out zh);
            _descEn.TryGetValue(key, out enDesc);
            //外部语言：说明了才有得翻（zh/enDesc 可能为 null）
            string ov = Pack(zh, enDesc);
            if (ov != null) { _descTextCache[e] = ov; return ov; }
            //"" = 显式无说明（Destroys Blocks\Enabled 就是这种），不算缺失；null 也照缓存（值可为 null）
            string v = en ? (enDesc ?? zh ?? null) : (zh ?? enDesc ?? null);
            _descTextCache[e] = v;
            return v;
        }

        //条目默认值后缀（"默认: X" / "Default: X"）：按 entry 缓存。
        //仍走 T() 而不是自己判 en, 这样外部语言包能翻译这串；语言/语言包一变，
        //NameCacheSync 会清空 _defSuffix，下次重新走 T()。
        internal static string DefaultSuffix(ConfigEntryBase entry) {
            NameCacheSync();
            string cached;
            if (_defSuffix.TryGetValue(entry, out cached)) return cached;
            string dft = FormatDefaultValue(entry.DefaultValue);
            cached = string.IsNullOrEmpty(dft) ? "" : T("默认: " + dft, "Default: " + dft);
            _defSuffix[entry] = cached;
            return cached;
        }

        //名称列悬浮说明 = 说明 + 默认值 + 恢复提示：三段在语言不变时都是常量 -> 整串按 entry 缓存。
        internal static string RowTip(ConfigEntryBase entry, string desc) {
            NameCacheSync();
            string cached;
            if (_rowTipCache.TryGetValue(entry, out cached)) return cached;
            string defText = FormatDefaultValue(entry);   //ConfigEntryBase 重载：bool 显示 开/关
            string tip = desc != null ? desc : "";
            if (defText.Length > 0) tip += "\n" + T("默认: " + defText, "Default: " + defText);
            tip += "\n" + T("点击恢复默认值", "Click to reset to default");
            _rowTipCache[entry] = tip;
            return tip;
        }

        //common key names in Chinese (falls back to the English enum name)
        public static string KeyDisplayName(KeyCode k) {
            string en = k.ToString();
            string zh = KeyZh(k);
            string ov = Pack(zh, en);   //外部语言：可按中文键名或 KeyCode 原名覆盖
            if (ov != null) return ov;
            //英文模式：直接显示 KeyCode 枚举名（本身就是英文，如 LeftAlt/Return/Space）
            return UseEn ? en : (zh ?? en);
        }

        //键位中文名（没有专门译名时返回 null -> 显示 KeyCode 原名）
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
                    //F1-F12 / 数字键：没有中文译名 -> 返回 null，显示 KeyCode 原名
                    return null;
            }
        }

        public static string MapNameZh(string scene) {
            string zh = MapZh(scene);
            string ov = Pack(zh, scene);   //外部语言：可按中文关卡名或场景原名覆盖
            if (ov != null) return ov;
            //英文模式：显示场景原名（本来就是英文）
            return UseEn ? scene : (zh ?? scene);
        }

        //关卡场景名（如 "Farm"/"Rooftops"）-> 中文关卡名（复用 _enumZh 映射，转大写匹配）
        private static string MapZh(string scene) {
            if (string.IsNullOrEmpty(scene)) return null;
            string zh;
            if (_enumZh.TryGetValue(scene.ToUpperInvariant(), out zh)) return zh;
            if (scene == "Treehouse") return "树屋";
            if (scene == "Lobby") return "大厅";
            return null;
        }

        //footer status: current game mode · current scene (e.g. 自由·农场 / 自由·树屋)
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
            // **V3 修复**：场景名查询补上 try。本文件与全项目的既定约定是"对 Unity 原生调用一律包裹"
            //（Env.SceneName 都显式保护），而这里原来是裸调；调用点 SR.Window.cs 的
            //  绘制体只有 finally 没有 catch -> 一旦抛异常会跳过本帧剩余绘制（缩放角、自绘光标、tooltip、
            //  以及之后的 DrawOverlays）并冒泡出 OnGUI。
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name; } catch (Exception __ex) { Guard.Log("ModeLabel 场景名", __ex); }
            return mode + "·" + MapNameZh(scene);
        }

        // SR_UCH UI language: 中文/English switch（设置页配置，运行时立即生效）
        //外部模块页面豁免：渲染时 _forceZh=true，T() 始终返回中文
        private static bool _langEn = true; //默认英文（设置页可切换；外部模块页面始终中文）
        private static bool _forceZh = false;
        private static ConfigEntry<string> _langEntry;

        // 外部语言包（一个 DLL = 一门语言，以此类推）
        //内置两门（中文/English）+ 任意多个外部语言包。语言包把词条表交给 SR，
        //所有文案出口（T / 键名 / 说明 / 枚举名 / 关卡名 / 键位名）统一先查它，
        //没收录的词条自动回退内置语言 -> 翻译不全也能正常玩，语言包可以增量补全。
        internal const string LangZh = "中文";
        internal const string LangEn = "English";
        private static readonly Dictionary<string, ILanguagePack> _packs = new Dictionary<string, ILanguagePack>();
        private static ILanguagePack _pack;      //当前生效的外部语言包（内置中/英时为 null）
        private static bool _fallbackEn;         //外部语言未收录时回退英文（否则回退中文）
        private static string _langId = LangEn;
        private static int _langVer = 0;         //语言版本号：切语言 / 注册语言包 -> 让显示名缓存整体失效

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

        //注册外部语言包（MainPlugin 扫描 plugins 目录时调用；带 [BepInPlugin] 的插件也可自己调）。
        //id 重复 = 覆盖；与内置语言重名则忽略（否则下拉框会出现两个中文）。
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

        //按 cfg 里的语言 id 重新解析当前语言（启动 / 切语言 / 注册语言包都走这里）。
        //语言包被删掉后 cfg 里仍留着它的 id -> 这里自动回退内置英文，不会显示成空白。
        public static void ApplyLanguage() {
            string id = null;
            try { if (_langEntry != null) id = _langEntry.Value; } catch (Exception __ex) { Guard.Log("ApplyLanguage", __ex); }
            if (string.IsNullOrEmpty(id)) id = LangEn;
            ILanguagePack p;
            if (_packs.TryGetValue(id, out p)) {
                _pack = p;
                _langId = id;
                _langEn = false;                 //不是内置英文
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
            _langVer++;   //显示名 / 说明缓存按语言失效
        }

        //当前语言 id（内置 "中文" / "English"，或外部语言包的 Id）。供功能按语言做缓存失效等
        public static string CurrentLanguageId { get { return _langId; } }

        //语言版本号：切语言 / 注册语言包都会 +1。
        //注意：不能用 _langEn 判断"语言变了", 外部语言包下 _langEn 恒为 false，
        //从中文切到语言包时 _langEn 没变，但界面文案（以及它的文字宽度）全变了。
        //凡是按语言缓存"测量结果"（列宽/文本高度）的地方都要比较这个版本号。
        public static int LangVer { get { return _langVer; } }

        //当前是否英文系：内置英文，或外部语言包声明回退英文。外部模块页强制中文时为 false。
        //原来各处直接写 (_langEn && !_forceZh)，加了语言包后统一走这里。
        private static bool UseEn {
            get { return (_langEn || _fallbackEn) && !_forceZh; }
        }

        //外部语言覆盖：先按中文原文查，再按英文原名查（英文原名用于枚举名 / 场景名 / KeyCode 名
        //这类本身没有中文原文的词条）。外部模块页（_forceZh）不查, 保持始终中文的既有语义。
        //词条表引用按语言版本号缓存：T() 在页面渲染里每帧被调几十到上百次（IMGUI 一帧还有 Layout+Repaint
        //两趟），而语言包若把 Map 写成计算属性（return new Dictionary<...>）就等于每次重建整张表。
        //_pack 只在 _langVer 变化（切语言 / 注册语言包）时改变，所以缓存引用零行为变化。
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
            // **地基要求**：T() 是全局咽喉（一帧被调上百次，Layout 与 Repaint 各一趟），
            //  语言包的取词实现再怪也不能让异常冒到 UI, 取不到就整体回退内置语言。
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

        //枚举显示名：中文模式/外部模块页用中文映射；英文模式用游戏原名（动物/关卡/分数类型
        //的枚举原名本身就是英文，如 CHICKEN/FARM/win, 无需额外翻译）
        private static string EnumDisplayName(string n) {
            return EnumNameZh(n);
        }

        //公开的枚举中文名（外部模块等跨 DLL 使用）：中文模式查 _enumZh 映射，英文模式返回原名
        public static string EnumNameZh(string n) {
            string zh;
            _enumZh.TryGetValue(n, out zh);
            string ov = Pack(zh, n);   //外部语言：可按中文译名或枚举原名覆盖
            if (ov != null) return ov;
            if (UseEn) return n; //英文系（含语言包回退英文）：直接显示原名
            return zh ?? n;
        }

	}
}
