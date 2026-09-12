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

// ==== 分区：Localization（中英文界面：T() 入口 / Register 注册表 / 查表 fallback / 键名·关卡名·枚举名）====
// 分区名/条目名/悬浮说明不再集中在本文件 —— 每个功能在自己的 cs 里用
//      SR.LocSec / SR.LocKey / SR.LocDesc 注册（Bind 与文案写在同一文件）；本文件只留字典与查表逻辑。

        private static readonly Dictionary<string, string> _sectionZh = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        private static readonly Dictionary<string, string> _keyZh = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        //--- T6：界面文案自注册入口 ---------------------------------------------------
        //原来所有分区名/键名/悬浮说明都堆在本文件的 5 张表里；现在每个功能的文案都放在
        //功能自己的 cs 里（Bind 与文案同文件），本文件只剩「字典 + 这三个 Register + 查表 fallback」：
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
            if (zh != null) _keyZh[k] = zh;
            if (en != null) _keyEn[k] = en;
        }

        public static void LocDesc(string sec, string key, string zh, string en) {
            if (sec == null || key == null) return;
            string k = sec + "\t" + key;
            if (zh != null) _descZh[k] = zh;
            if (en != null) _descEn[k] = en;
        }

        //  中文模式：中文 → 英文覆盖 → key 原名；英文模式：英文覆盖 → key 原名。
        //  悬浮说明：中文模式 中文 → 英文；英文模式 英文 → 中文；都没有 = null（不显示）。
        private static string ZhSection(string sec) {
            string zh, en;
            _sectionZh.TryGetValue(sec, out zh);
            _sectionEn.TryGetValue(sec, out en);
            return (_langEn && !_forceZh) ? (en ?? sec) : (zh ?? en ?? sec);
        }

        //英文显示覆盖（英文模式侧边栏/分区标题用；T6 起由各功能文件 LocSec 注册）
        private static readonly Dictionary<string, string> _sectionEn = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        private static string ZhKey(ConfigEntryBase e) {
            string key = e.Definition.Section + "\t" + e.Definition.Key;
            string zh, en;
            _keyZh.TryGetValue(key, out zh);
            _keyEn.TryGetValue(key, out en);
            return (_langEn && !_forceZh) ? (en ?? e.Definition.Key) : (zh ?? en ?? e.Definition.Key);
        }

        //英文 key 显示覆盖（英文模式用；T6 起由各功能文件 LocKey 注册）
        private static readonly Dictionary<string, string> _keyEn = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        //悬浮说明（中文 tooltip；T6 起由各功能文件 LocDesc 注册，null = 无说明）
        private static readonly Dictionary<string, string> _descZh = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        //悬浮说明（英文 tooltip；与 _descZh 条目一一对应，英文模式查这张表）
        private static readonly Dictionary<string, string> _descEn = new Dictionary<string, string>(); //T6：内容已下放到各功能文件（LocSec/LocKey/LocDesc 注册）

        private static string ZhDesc(ConfigEntryBase e) {
            string key = e.Definition.Section + "\t" + e.Definition.Key;
            string zh, en;
            _descZh.TryGetValue(key, out zh);
            _descEn.TryGetValue(key, out en);
            //"" = 显式「无说明」（Destroys Blocks\Enabled 就是这种），不算缺失
            return (_langEn && !_forceZh) ? (en ?? zh ?? null) : (zh ?? en ?? null);
        }

        //common key names in Chinese (falls back to the English enum name)
        public static string KeyDisplayName(KeyCode k) {
            //英文模式：直接显示 KeyCode 枚举名（本身就是英文，如 LeftAlt/Return/Space）
            if (_langEn && !_forceZh) return k.ToString();
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
                    if (k >= KeyCode.F1 && k <= KeyCode.F12) return k.ToString();
                    if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
                    return k.ToString();
            }
        }

        public static string MapNameZh(string scene) {
            //英文模式：显示场景原名（本来就是英文）
            if (_langEn && !_forceZh) return scene;
            //关卡场景名（如 "Farm"/"Rooftops"）→ 中文关卡名（复用 _enumZh 映射，转大写匹配）
            if (!string.IsNullOrEmpty(scene)) {
                string zh;
                if (_enumZh.TryGetValue(scene.ToUpperInvariant(), out zh)) return zh;
            }
            switch (scene) {
                case "Treehouse": return "树屋";
                case "Lobby": return "大厅";
                default: return scene;
            }
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
            } catch { }
            string scene = SceneManager.GetActiveScene().name;
            return mode + "·" + MapNameZh(scene);
        }

        //--- SR_UCH UI language: 中文/English switch（设置页配置，运行时立即生效）---
        //外部模块页面豁免：渲染时 _forceZh=true，T() 始终返回中文
        private static bool _langEn = true; //默认英文（设置页可切换；外部模块页面始终中文）
        private static bool _forceZh = false;
        private static ConfigEntry<string> _langEntry;

        //界面当前是否显示英文（外部模块页面强制中文时返回 false）。供 Loc 等公共入口查询
        public static bool IsEnglishUi() {
            return _langEn && !_forceZh;
        }

        public static string T(string zh, string en) {
            return (_langEn && !_forceZh) ? en : zh;
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
        //的枚举原名本身就是英文，如 CHICKEN/FARM/win —— 无需额外翻译）
        private static string EnumDisplayName(string n) {
            return EnumNameZh(n);
        }

        //公开的枚举中文名（外部模块等跨 DLL 使用）：中文模式查 _enumZh 映射，英文模式返回原名
        public static string EnumNameZh(string n) {
            if (_langEn && !_forceZh) return n; //英文模式：直接显示原名
            string zh;
            return _enumZh.TryGetValue(n, out zh) ? zh : n;
        }

	}
}
