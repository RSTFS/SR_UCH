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

// ==== 分区：Localization（中英文界面：T() 入口 / 分区·条目·描述·枚举翻译表 / 键名·关卡名）====
// 新增翻译：在此文件的字典中按 "分区\t键名" 添加 中/英 条目即可；新功能文本用 Loc.T(zh, en)。

        //--- Chinese names for SR_UCH sections and entry keys (external mods stay English) ---
        private static readonly Dictionary<string, string> _sectionZh = new Dictionary<string, string> {
            { "Destroy Blocks", "方块破坏" },
            { "Builder Enhancements", "建造增强" },
            { "Player Tracker", "移动轨迹" },
            { "Treehouse Suicide", "快速自杀" },
            { "EX", "EX" },
            { "实验", "实验" },
            { "Respawn", "重生" },
            { "首页", "首页" },
            { "更多玩家", "更多联机" },
            { "模组联机", "模组联机" },
        };

        private static readonly Dictionary<string, string> _keyZh = new Dictionary<string, string> {
            { "Destroy Blocks\tToggle Key", "切换键" },
            { "Destroy Blocks\tDelete Key", "删除键" },
            { "Destroy Blocks\tAllow Clients", "允许客户端删除" },
            { "Destroy Blocks\tEnabled", "方块破坏总开关" },
            { "Destroy Blocks\tSelect Mode", "选择模式" },
            { "Destroy Blocks\tList Mode", "列表模式" },
            { "Destroy Blocks\tTrack Player", "追踪玩家" },
            { "Builder Enhancements\tCollision Override", "无视碰撞" },
            { "Builder Enhancements\tCollision Toggle Key", "无视碰撞开关" },
            { "Player Tracker\tTracking Length", "轨迹长度" },
            { "Player Tracker\tSkip Frames", "跳帧数" },
            { "Player Tracker\tLine Start Width", "起点宽度" },
            { "Player Tracker\tLine End Width", "终点宽度" },
            { "Player Tracker\tEnabled", "移动轨迹总开关" },
            { "Treehouse Suicide\tKeybind", "自杀键（组合键）" },
            { "Treehouse Suicide\tEnabled", "快速自杀总开关" },
            { "EX\tInvincible", "无敌" },
            { "EX\tFly", "飞天" },
            { "EX\tIgnore Host Limit", "无视房主限制" },
            { "EX\tScore Type", "加分类型" },
            { "EX\tIgnore Mode Limit", "无视模式限制" },
            { "EX\tFreeze Character", "冻结角色" },
            { "EX\tAllow Clients", "允许客户端删除" },
            { "Respawn\tSpawn Immunity", "重生无敌时间" },
            { "Respawn\tDelay", "重生延迟" },
            { "Respawn\tEnabled", "重生功能总开关" },
            { "Respawn\tSet Spawn Key", "设置重生点键" },
            { "Respawn\tRespawn Key", "重生键" },
            { "Respawn\tReset Spawn Keys", "恢复重生点键" },
            { "Respawn\tSpawn Points Enabled", "重生点总开关" },
            { "视野\t自由相机", "自由相机" },
            { "视野\tFOV", "视野大小" },
            { "视野\tFOV Key", "视野快捷键" },
            { "地图\tMap Key", "地图按键" },
            { "设置\tUI Scale", "界面缩放" },
            { "设置\tMap Key", "地图按键" },
            { "设置\tDisabled Plugins", "禁用的外部插件" },
            { "设置\tOpen Key", "打开键" },
            { "设置\tBlock Input", "冻结输入" },
            { "设置\tWindow Width", "窗口宽度" },
            { "设置\tWindow Height", "窗口高度" },
            { "设置\tWindow X", "窗口X" },
            { "设置\tWindow Y", "窗口Y" },
            { "地图\t同步循环", "同步循环" },
            { "地图\t加载后清理", "加载后清理" },
            { "实验\tNet Optimize", "位置同步" },
            { "实验\tSync Frequency", "同步频率" },
            { "实验\tSync All", "同步范围" },
            { "实验\tQuick Switch", "快速切换" },
            { "实验\tQuick Switch Key", "切换键" },
            { "实验\tScore Discount", "分数折扣" },
            { "实验\tMore Discount Values", "更多折扣数值" },
            { "实验\tReload Mode", "重载模式" },
            { "实验\tTreehouse Map", "树屋地图" },
            { "实验\tQuestion Mark", "树屋问号" },
            { "实验\tQuestion Level", "问号关卡" },
            { "实验\tCheat Flag", "作弊标识" },
            { "实验\tMute Own", "关闭自己的声音" },
            { "实验\tMute Others", "关闭其它玩家的声音" },
        };

        private static string ZhSection(string sec) {
            //英文模式：显示配置原名（英文 section）；中文 section 名映射为英文
            if (_langEn && !_forceZh) {
                string en;
                if (_sectionEn.TryGetValue(sec, out en)) return en;
                return sec;
            }
            if (sec == "会话内容") return "会话内容";
            if (sec == "地图") return "地图与关卡";
            if (sec == "快速调整") return "快速调整";
            string zh;
            return _sectionZh.TryGetValue(sec, out zh) ? zh : sec;
        }

        //中文 section 名 → 英文（英文模式侧边栏/分区标题用）
        private static readonly Dictionary<string, string> _sectionEn = new Dictionary<string, string> {
            { "视野", "Camera" },
            { "快速调整", "Quick Adjust" },
            { "地图", "Map" },
            { "实验", "Experiments" },
            { "尝试计数", "Attempts" },
            { "会话内容", "Chat" },
            { "更多玩家", "More Online" },
            { "模组联机", "Mod Lobby" },
            { "首页", "Home" },
        };

        private static string ZhKey(ConfigEntryBase e) {
            //英文模式：英文 key 显示原名；中文 key（如"解除建造上限"）查英文表
            if (_langEn && !_forceZh) {
                string en;
                if (_keyEn.TryGetValue(e.Definition.Section + "\t" + e.Definition.Key, out en)) return en;
                return e.Definition.Key;
            }
            string zh;
            return _keyZh.TryGetValue(e.Definition.Section + "\t" + e.Definition.Key, out zh) ? zh : e.Definition.Key;
        }

        //中文 key 名 → 英文（英文模式显示；英文 key 直接显示原名，无需映射）
        private static readonly Dictionary<string, string> _keyEn = new Dictionary<string, string> {
            { "Builder Enhancements\t解除建造上限", "Lift Build Cap" },
            { "Builder Enhancements\t上限数值", "Limit Value" },
            { "视野\t自由相机", "Free Camera" },
            { "视野\tFOV", "FOV" },
            { "视野\tFOV Key", "FOV Key" },
            { "地图\t同步循环", "Sync Cycles" },
            { "地图\t加载后清理", "GC after load" },
            { "地图\t地图总开关", "Map Master Switch" },
        };

        private static readonly Dictionary<string, string> _descZh = new Dictionary<string, string> {
            { "Destroy Blocks\tToggle Key", "按住进入删除模式并高亮最近放置的方块（松开退出）" },
            { "Destroy Blocks\tDelete Key", "删除当前选中的方块（鼠标滚轮切换选择）" },
            { "Destroy Blocks\tAllow Clients", "是否允许非房主玩家也删除方块（由房主同步）" },
            { "Destroy Blocks\tSelect Mode", "选择模式：距离 = 按离自己距离排序（初始最近）；放置顺序 = 最后放的先选" },
            { "Destroy Blocks\tList Mode", "列表模式：普通 = 只列出玩家确切放置过的方块；进阶 = 所有方块单独列出" },
            { "Destroy Blocks\tTrack Player", "追踪玩家：不追踪 = 找所有玩家的方块；#1 = 只找玩家1的方块，#2/#3/#4 以此类推（只影响列表，配合列表模式使用）" },
            { "Destroy Blocks\tEnabled", "" },
            { "Builder Enhancements\tCollision Override", "无视碰撞：方块无视放置规则，可以放置在任何位置（重叠/空中/交叉）" },
            { "Builder Enhancements\tCollision Toggle Key", "游戏内切换无视碰撞覆盖（默认 F1）" },
            { "Builder Enhancements\t解除建造上限", "解除关卡满度限制：开启后树屋保存/发布界面的满度上限从原版 500 提高到“上限数值”（默认 1000000），超满的关卡也能正常发布/上传。\n无需按键，在树屋外开启也会生效；关闭立即恢复原版 500。" },
            { "Builder Enhancements\t上限数值", "满度上限数值（500 - 10000000；游戏原版为 500）。\n修改后立即生效；数值越大，能放的方块越多后仍可发布。" },
            { "Player Tracker\tTracking Length", "轨迹长度：记录多长时间的移动轨迹（60 格 ≈ 1 秒）" },
            { "Player Tracker\tSkip Frames", "跳帧数：每跳过 N 帧才记录一个位置点（越大轨迹越稀疏）" },
            { "Player Tracker\tLine Start Width", "轨迹起点宽度（越靠近当前位置越宽/越细）" },
            { "Player Tracker\tLine End Width", "轨迹终点宽度（最旧位置点的宽度）" },
            { "Player Tracker\tEnabled", "移动轨迹总开关：开启后在画面上绘制其他玩家的移动轨迹线" },
            { "Treehouse Suicide\tKeybind", "自杀键（组合键）：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键即可设为组合键（默认 Shift+0）" },
            { "Treehouse Suicide\tEnabled", "快速自杀总开关：开启后按组合键（默认 Shift+0）快速自杀" },
            { "EX\tInvincible", "无敌：免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）" },
            { "EX\tFly", "飞天：方向键自由飞行（上/下升降，左/右平移，按住 Shift 加速，不按键则悬浮空中）" },
            { "EX\tIgnore Host Limit", "无视房主房客限制：开启后房客也能执行房主限制的操作（如树屋问号添加/删除等）" },
            { "EX\tScore Type", "加分的分数类型（获胜/陷阱击杀/第一/金币等）" },
            { "实验\tNet Optimize", "位置同步：按设定频率主动上报自己的位置，让其他玩家看到你的移动更平滑更跟手。原理：游戏默认只在关键事件时同步位置，开启后按固定频率持续上报。仅对局内生效；本地派对/单机无网络时无效果。" },
            { "实验\tSync Frequency", "同步频率（10 - 50 Hz，默认 20）：每秒钟上报多少次位置。越高 → 其他玩家看到的你越平滑跟手，但占用更多带宽与 CPU；越低 → 更省流量，但对方看到的移动可能一顿一顿。联机延迟高时建议调低。" },
            { "实验\tSync All", "同步范围：开 = 把所有玩家的位置都按频率同步（需房主权限，适合低延迟局域网/本地派对联机）；关 = 只同步你自己（默认，推荐，其他玩家由各自客户端上报）。" },
            { "实验\tQuick Switch", "快速切换：自由模式内按下切换键直接切换 行动↔建造 模式（无需按 B）。支持设置切换耗时（见“切换耗时”）" },
            { "实验\tQuick Switch Key", "快速切换键（默认 LeftCtrl；按下它立即切换行动/建造模式；Esc 可清空为未设置）" },
            { "实验\tQuick Switch Time", "切换耗时（秒）：按住切换键多久后切换；0 = 立即切换（默认）" },
            { "实验\tScore Discount", "评分折扣 %：把自己的得分平衡板 handicap 设为 100-折扣 %。\n默认滑块（0-90 整十倍数，默认 20 → handicap 80%）；勾选「更多折扣数值」后变为自由输入框（0-90 任意整数，0 = 关闭）。\n⚠ 游戏 handicap 内部按四舍五入取整十倍数：非整十数值只改显示，实际结算按四舍五入后的整十倍数生效（如 85 → 90、84 → 80）。\n90 以上钳到 handicap 10 = 上限 90%；0 = 关闭。平衡板上自己那一行显示对应百分比；可随时点“恢复原值”还原。" },
            { "实验\tMore Discount Values", "更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n⚠ 游戏本身的 handicap 不支持任意百分比——内部按四舍五入取整十倍数（如 85 → 90、84 → 80）。开启后自由输入的非整十数值只修改显示，实际游戏结算仍按四舍五入后的整十倍数生效。" },
            { "实验\tReload Mode", "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。" },
            { "实验\tGrid Always On", "地图网格：行动状态下也显示建造网格（游戏默认只在建造阶段显示）。\n随开随关：开启立即淡入，关闭立即淡出；任何模式都生效。\n（开关在地图栏目里）" },
            { "实验\tTreehouse Map", "树屋地图：在树屋大厅也能打开地图（M 键开，左键拖拽平移，T 传送到鼠标位置）" },
            { "实验\tQuestion Mark", "树屋问号总开关：给指定关卡的门添加问号（未解锁的关卡不能添加）。\n仅房主可操作；四个按钮（添加/删除/全部添加/清除全部）都受本开关控制。" },
            { "实验\tQuestion Level", "要添加问号的关卡（配合 添加问号 / 删除问号 使用）" },
            { "实验\tCheat Flag", "显示当前存档是否被标记为作弊（使用过作弊码后无法解锁全部成就）" },
            { "实验\tSelf Invincible", "自身无敌（仅自由模式有效）：免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）；只作用于自己。" },
            { "实验\tSelf Fly", "自身飞天（仅自由模式有效）：方向键自由飞行，按住 Shift 加速，不按键悬浮空中；只作用于自己。" },
            { "实验\tSelf Crouch Move", "自身蹲移（仅自由模式有效）：蹲下时也能左右移动（A/D 或方向键）；只作用于自己。" },
            { "实验\tMute Own", "关闭自己的声音：静音自己角色的角色音效（走路/跳跃/落地/掉落等，由角色发出的声音）。其他人不受影响。" },
            { "实验\tMute Others", "关闭其它玩家的声音：静音其他玩家角色的角色音效（自己听不到，不影响对方）。" },
            { "Respawn\tSpawn Immunity", "重生后的无敌时长（秒）：复活后短时间内免疫伤害（仅自由模式）" },
            { "Respawn\tDelay", "死亡后多少秒重生（最小 0.1 秒）（仅自由模式）" },
            { "Respawn\tEnabled", "重生功能总开关：重生无敌时间 + 重生延迟（仅自由模式）" },
            { "Respawn\tSet Spawn Key", "在当前位置设置一个自定义重生点（默认 O）（仅自由模式）" },
            { "Respawn\tRespawn Key", "传送到最近的自定义重生点，没有则去游戏默认起点（默认 P）（仅自由模式）" },
            { "Respawn\tReset Spawn Keys", "删除所有自定义重生点，保留游戏默认起点（默认 K）（仅自由模式）" },
            { "Respawn\tSpawn Points Enabled", "重生点功能总开关：设置重生点 / 瞬移重生 / 恢复重生点（仅自由模式）" },
            { "视野\t自由相机", "自由相机：开启后滚轮缩放视野（FOV 1-20）；关闭后完全恢复游戏默认相机。\n任何模式/场景都可用，但挑战模式对局内自动禁用。" },
            { "视野\tFOV", "视野大小（1 - 20）：数值越小拉得越近，越大看得越广（自由相机开启时生效，滚轮同步）" },
            { "视野\tFOV Key", "按键切换自由相机：每次启动恢复游戏默认（任何模式/场景都可用，挑战模式对局内禁用）" },
            { "地图\tMap Key", "打开/关闭地图窗口（俯视图，可看全图/设重生点；仅自由模式可用，实验栏可解锁树屋；挑战模式对局内禁用）" },
            { "设置\tUI Scale", "界面整体缩放大小（1.0 = 100%，1.3 默认）" },
            { "设置\tMap Key", "打开/关闭地图窗口（仅自由模式可用，实验栏可解锁树屋；挑战模式对局内禁用）" },
            { "设置\tDisabled Plugins", "被禁用的外部插件（GUID 列表，分号分隔，重启后仍禁用）" },
            { "设置\tOpen Key", "打开/关闭配置管理器（默认 Insert）" },
            { "设置\tBlock Input", "打开管理器时冻结游戏输入（防止误操作角色）" },
            { "设置\tWindow Width", "管理器窗口宽度（400 - 1200）" },
            { "设置\tWindow Height", "管理器窗口高度（300 - 1000）" },
            { "设置\tWindow X", "管理器窗口 X 坐标（屏幕左上角为原点）" },
            { "设置\tWindow Y", "管理器窗口 Y 坐标（屏幕左上角为原点）" },
            { "地图\t同步循环", "同步循环：强制所有发射器（炮弹/火焰等）的初始延迟统一为 0.5 秒。\n原版自由模式下延迟会减去网络延迟（0.5 - ping），高 ping 时发射节奏不稳定；开启后固定 0.5 秒，全员发射时机一致。" },
            { "地图\t地图总开关", "地图总开关：关闭后无法打开地图窗口（M 键无效），已打开的地图立即关闭。\n「地图网格」「同步循环」「树屋地图」等独立功能不受影响。" },
        };

        //英文描述（tooltip）：与 _descZh 条目一一对应；英文模式查这张表
        private static readonly Dictionary<string, string> _descEn = new Dictionary<string, string> {
            { "Destroy Blocks\tToggle Key", "Hold to enter delete mode and highlight the most recently placed block (release to exit)" },
            { "Destroy Blocks\tDelete Key", "Delete the currently selected block (mouse wheel cycles selection)" },
            { "Destroy Blocks\tAllow Clients", "Allow non-host players to destroy blocks too (synced through the host)" },
            { "Destroy Blocks\tSelect Mode", "Select mode: Distance = sorted by distance to you (nearest first); Placement = most recently placed first" },
            { "Destroy Blocks\tList Mode", "List mode: Normal = only blocks actually placed by players; Advanced = every block listed individually" },
            { "Destroy Blocks\tTrack Player", "Track player: NoTrack = find every player's blocks; #1 = only blocks placed by player 1, #2/#3/#4 likewise (affects the list, used with list mode)" },
            { "Destroy Blocks\tEnabled", "" },
            { "Builder Enhancements\tCollision Override", "Ignore collision: pieces ignore placement rules and can go anywhere (overlap/air/crossing)" },
            { "Builder Enhancements\tCollision Toggle Key", "Toggle ignore-collision in-game (default F1)" },
            { "Builder Enhancements\t解除建造上限", "Lift the level fullness cap: the treehouse save/publish cap rises from 500 to the Limit value (default 1000000), so over-full levels can be published.\nWorks from anywhere; turning it off restores 500." },
            { "Builder Enhancements\t上限数值", "Fullness cap value (500 - 10000000; vanilla is 500).\nTakes effect immediately; higher allows more blocks before publish is blocked." },
            { "Player Tracker\tTracking Length", "How long of a movement trail to record (60 ticks ≈ 1 second)" },
            { "Player Tracker\tSkip Frames", "Record a point every N frames (higher = sparser trail)" },
            { "Player Tracker\tLine Start Width", "Trail start width (near the current position)" },
            { "Player Tracker\tLine End Width", "Trail end width (at the oldest point)" },
            { "Player Tracker\tEnabled", "Master switch: draw movement trails for other players on screen" },
            { "Treehouse Suicide\tKeybind", "Suicide key (combo): click the button, hold Shift/Ctrl/Alt while pressing the main key (default Shift+0)" },
            { "Treehouse Suicide\tEnabled", "Master switch: use the suicide combo (default Shift+0) to kill yourself fast" },
            { "EX\tInvincible", "Invincible: immune to all non-forced deaths (traps/bullets/pits/punches etc.)" },
            { "EX\tFly", "Fly: arrow keys move freely in the air, hold Shift to sprint, no key = hover" },
            { "EX\tIgnore Host Limit", "Ignore host/guest limits: guests can use host-only operations (e.g. treehouse question marks)" },
            { "EX\tScore Type", "Score type to award (win/trap/first/coin etc.)" },
            { "实验\tNet Optimize", "Position sync: actively report your position at a fixed rate so others see smoother movement.\nThe game normally syncs only on key events; this pushes it every tick.\nIn-match only; no effect in local party / single player." },
            { "实验\tSync Frequency", "Sync frequency (10 - 50 Hz, default 20): how many position reports per second. Higher = smoother to others but more bandwidth/CPU; lower = lighter but choppier. Lower it on laggy connections." },
            { "实验\tSync All", "Sync scope: ON = sync everyone's positions (needs host authority, good for low-latency LAN/local party); OFF = only yourself (default, recommended, others report themselves)." },
            { "实验\tQuick Switch", "Quick switch: in freeplay, press the key to swap Action<->Build modes instantly (no B key needed). Optional hold delay (see switch time)." },
            { "实验\tQuick Switch Key", "Quick switch key (default LeftCtrl; press to swap Action/Build; Esc clears to unset)" },
            { "实验\tQuick Switch Time", "Switch delay (seconds): how long to hold before switching; 0 = instant (default)" },
            { "实验\tScore Discount", "Score discount %: set your score-balancer handicap to 100-discount %.\nDefault: slider (0-90 in tens, default 20 → handicap 80%); tick More Discount Values for a free input box (0-90 any integer, 0 = off).\n⚠ The game's handicap rounds to the nearest multiple of 10 internally: non-multiples only change display, the tally uses the rounded value (85 → 90, 84 → 80).\n90+ clamps to handicap 10 = 90% max; 0 = off. Your balancer row shows the percentage; use Restore to reset." },
            { "实验\tMore Discount Values", "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).\n⚠ The game's handicap does not support arbitrary percentages - it rounds to the nearest multiple of 10 internally (85 → 90, 84 → 80). Non-multiple values only change the display; the actual tally still uses the rounded multiple of 10." },
            { "实验\tReload Mode", "Reload level mode:\nKeep blocks & score (allow fill) = blocks are kept; the host broadcasts a fill using the pre-reload per-type blocks (win/coin/trap etc. as-is), so everyone (incl. clients without the mod) sees the same score and types as before at the next round's tally (fill does not trigger an immediate tally; icons appear with the normal round end).\nKeep blocks only (skip fill) = blocks are kept, score resets (fresh match, no fill)." },
            { "实验\tGrid Always On", "Map grid: show the build grid during action phase too (vanilla only shows it while building).\nToggles instantly; works in every mode.\n(Switch lives in the Map tab)" },
            { "实验\tTreehouse Map", "Treehouse map: open the map in the treehouse lobby (M to open, drag to pan, T to teleport to cursor)" },
            { "实验\tQuestion Mark", "Treehouse question mark master switch: add a question mark to a level's portal (locked levels can't).\nHost-only; all four buttons (add/remove/add all/clear all) are gated by this switch." },
            { "实验\tQuestion Level", "Which level to mark with a question mark (used with Add / Remove)" },
            { "实验\tCheat Flag", "Show whether this save is flagged as a cheater (cheat codes lock achievements)" },
            { "实验\tSelf Invincible", "Self invincible (freeplay only): immune to all non-forced deaths; affects only you." },
            { "实验\tSelf Fly", "Self fly (freeplay only): arrow keys fly, hold Shift to sprint, no key = hover; affects only you." },
            { "实验\tSelf Crouch Move", "Self crouch-move (freeplay only): move left/right while ducking (A/D or arrows); affects only you." },
            { "实验\tMute Own", "Mute your own character's sounds (walk/jump/land/fall etc. emitted by the character). Others are not affected." },
            { "实验\tMute Others", "Mute other players' character sounds (you won't hear them; their clients are unaffected)." },
            { "Respawn\tSpawn Immunity", "Invincibility seconds after respawn (freeplay only)" },
            { "Respawn\tDelay", "Seconds before respawn after death (min 0.1) (freeplay only)" },
            { "Respawn\tEnabled", "Master switch: respawn invincibility + respawn delay (freeplay only)" },
            { "Respawn\tSet Spawn Key", "Set a custom spawn point at your position (default O) (freeplay only)" },
            { "Respawn\tRespawn Key", "Teleport to the nearest custom spawn, else the game default start (default P) (freeplay only)" },
            { "Respawn\tReset Spawn Keys", "Remove all custom spawn points, keep the game default (default K) (freeplay only)" },
            { "Respawn\tSpawn Points Enabled", "Master switch: set spawn / teleport respawn / reset spawn (freeplay only)" },
            { "视野\t自由相机", "Free camera: wheel zooms the view (FOV 1-20); off fully restores the game camera (works in any mode/scene, auto-disabled in Challenge matches)" },
            { "视野\tFOV", "FOV (1 - 20): lower zooms in, higher sees more (applies when free camera is on; wheel syncs)" },
            { "视野\tFOV Key", "Key to toggle free camera; each game start resets to default (works anywhere, disabled in Challenge matches)" },
            { "地图\tMap Key", "Open/close the map window (top view: full level / spawn points; freeplay only unless unlocked in Experiments; disabled in Challenge matches)" },
            { "设置\tUI Scale", "UI zoom (1.0 = 100%, 1.3 default)" },
            { "设置\tMap Key", "Open/close the map window (freeplay only unless unlocked in Experiments; disabled in Challenge matches)" },
            { "设置\tDisabled Plugins", "Disabled external plugins (GUID list, semicolon-separated, persists across restarts)" },
            { "设置\tOpen Key", "Open/close the config manager (default Insert)" },
            { "设置\tBlock Input", "Freeze game input while the manager is open (prevents accidental character control)" },
            { "设置\tWindow Width", "Manager window width (400 - 1200)" },
            { "设置\tWindow Height", "Manager window height (300 - 1000)" },
            { "设置\tWindow X", "Manager window X (origin at top-left of screen)" },
            { "设置\tWindow Y", "Manager window Y (origin at top-left of screen)" },
            { "地图\t同步循环", "Sync cycles: force every launcher (cannons/fire etc.) initial delay to a fixed 0.5s.\nVanilla freeplay subtracts ping (0.5 - ping), so high ping makes launch timing unstable; with this ON the delay is a fixed 0.5s for everyone." },
            { "地图\t地图总开关", "Map master switch: OFF disables opening the map (M key does nothing); an open map closes immediately.\nIndependent features like Map grid / Sync cycles / Treehouse map are not affected." },
        };

        private static string ZhDesc(ConfigEntryBase e) {
            string key = e.Definition.Section + "\t" + e.Definition.Key;
            //英文模式：查英文描述表（查不到则留空，不显示中文）
            if (_langEn && !_forceZh) {
                string en;
                return _descEn.TryGetValue(key, out en) ? en : null;
            }
            string zh;
            return _descZh.TryGetValue(key, out zh) ? zh : null;
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
        //EX 附加页豁免：渲染时 _forceZh=true，T() 始终返回中文
        private static bool _langEn = true; //默认英文（设置页可切换；EX 附加页始终中文）
        private static bool _forceZh = false;
        private static ConfigEntry<string> _langEntry;

        //界面当前是否显示英文（EX 附加页强制中文时返回 false）。供 Loc 等公共入口查询
        public static bool IsEnglishUi() {
            return _langEn && !_forceZh;
        }

        public static string T(string zh, string en) {
            return (_langEn && !_forceZh) ? en : zh;
        }

        private static readonly Dictionary<string, string> _enumZh = new Dictionary<string, string> {
            { "PUBLIC", "公开" }, { "FRIENDS", "仅限朋友" }, { "PRIVATE", "仅邀请" }, { "INVISIBLE", "隐身" },
            { "Fun", "好玩" }, { "Competitive", "竞技" }, { "Beginner", "新手" }, { "CustomLevels", "自定义关卡" },
            { "win", "获胜" }, { "winDead", "获胜（死亡）" }, { "soloWin", "单挑获胜" }, { "first", "第一" },
            { "trap", "陷阱击杀" }, { "suicide", "自杀" }, { "comeback", "逆转" }, { "coin", "金币" },
            { "second", "第二" }, { "third", "第三" }, { "fourth", "第四" },
            { "NONE", "无" }, { "None", "无" }, { "Shift", "Shift" }, { "Ctrl", "Ctrl" }, { "Alt", "Alt" },
            { "CHICKEN", "鸡" }, { "HORSE", "马" }, { "SHEEP", "羊" }, { "RACCOON", "浣熊" },
            { "Placement", "放置顺序" }, { "Distance", "距离" }, { "Normal", "普通" }, { "Advanced", "进阶" },
            { "NoTrack", "不追踪" }, { "P1", "#1" }, { "P2", "#2" }, { "P3", "#3" }, { "P4", "#4" },
            { "KeepScore", "保留方块和分数" }, { "KeepBlocksOnly", "仅保留方块" },
            { "CHAMELEON", "变色龙" }, { "SQUIRREL", "松鼠" }, { "ROBOT", "机器兔子" }, { "ELEPHANT", "大象" },
            { "MONKEY", "猴子" }, { "SNAKE", "蛇" }, { "HIPPO", "河马" }, { "TURTLE", "乌龟" },
            { "PANDA", "熊猫" }, { "FOX", "狐狸" }, { "PLATYPUS", "鸭嘴兽" },
            { "FARM", "农场" }, { "ROOFTOPS", "屋顶" }, { "OLDMANSION", "老宅" }, { "WATERFALL", "瀑布" },
            { "PYRAMID", "金字塔" }, { "WINDMILL", "风车" }, { "METALPLANT", "金属工厂" }, { "ICEBERG", "冰山" },
            { "DANCEPARTY", "舞会" }, { "PIER", "码头" }, { "BLANKLEVEL", "空白" }, { "JUNGLETEMPLE", "丛林神庙" },
            { "VOLCANO", "火山" }, { "CRUMBLINGBRIDGE", "断桥" }, { "NUCLEARPLANT", "核电站" }, { "TRONLEVEL", "电子" },
            { "SPACELEVEL", "太空" }, { "BALLROOM", "舞厅" }, { "ROLLERCOASTER", "过山车" }, { "METRO", "地铁" },
            { "WATERTOWER", "水塔" }, { "RAFT", "木筏" }, { "PICTUREFRAME", "相框" }, { "SPACESTATION", "空间站" },
            { "RANDOM", "随机" },
            { "Wins", "胜场" }, { "Success", "成功" }, { "Deaths", "死亡" }, { "Coins", "金币" },
            { "LevelsPlayed", "关卡数" }, { "Rounds", "回合数" }, { "TimePlayed", "总时间" },
        };

        //枚举显示名：中文模式/EX 页用中文映射；英文模式用游戏原名（动物/关卡/分数类型
        //的枚举原名本身就是英文，如 CHICKEN/FARM/win —— 无需额外翻译）
        private static string EnumDisplayName(string n) {
            return EnumNameZh(n);
        }

        //公开的枚举中文名（EX 模块等跨 DLL 使用）：中文模式查 _enumZh 映射，英文模式返回原名
        public static string EnumNameZh(string n) {
            if (_langEn && !_forceZh) return n; //英文模式：直接显示原名
            string zh;
            return _enumZh.TryGetValue(n, out zh) ? zh : n;
        }

	}
}
