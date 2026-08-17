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

// ==== 分区：Pages（各栏目页面渲染：首页/地图/更多联机/模组联机/尝试计数/EX/实验/快速调整/会话内容）====

        //快速调整 page: 分数折扣 + 快速切换 + 快速自杀 (三个分区)
        private static void RenderQuickAdjustConsole() {
            GUILayout.Label(T("— 分数折扣 —", "— Score discount —"), _secHeader);
            //折扣数值：默认滑块（0/10/20...90 整十倍数，与其它滑块条一致的 DrawSlider 样式）；
            //「更多折扣数值」开 → 编辑框自由 0-90。更多折扣选择框放在"恢复原值"右侧。
            GUILayout.BeginHorizontal();
            ConfigEntryBase disc = FindInternalEntry("实验", "Score Discount");
            int discVal = Experiments.ScoreDiscount;
            bool moreOn = Experiments.MoreDiscountOn;
            GUILayout.Label(T("折扣 %", "Discount %"), _labelWrap, GUILayout.Width(Sc(64)), GUILayout.Height(Sc(52)));
            if (moreOn) {
                //更多折扣数值：编辑框自由 0-90
                string cur = discVal.ToString();
                string nv = GUILayout.TextField(cur, _searchBox, GUILayout.Width(Sc(80)), GUILayout.Height(Sc(26)));
                int parsed;
                if (nv != cur && int.TryParse(nv, out parsed)) {
                    parsed = Mathf.Clamp(parsed, 0, 90);
                    if (disc != null) SetValue(disc, parsed);
                }
            } else {
                //滑块：0-90 整十倍数（DrawSlider 样式，滚轮也支持）
                int slideVal = Mathf.Clamp((discVal + 5) / 10 * 10, 0, 90);
                Rect sr = GUILayoutUtility.GetRect(Sc(220), Sc(28));
                float nv = DrawSlider(sr, slideVal, 0f, 90f, true);
                int nslide = Mathf.RoundToInt(nv / 10f) * 10; //吸附整十
                if (nslide != slideVal && disc != null) SetValue(disc, nslide);
                GUILayout.Label(T(nslide + "%", nslide + "%"), _label, GUILayout.Width(Sc(52)), GUILayout.Height(Sc(26)));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("应用折扣", "Apply"), T("把自己 handicap 设为 100-折扣 %。\n滑块模式（0-90 整十）：0 = 关闭；90 → handicap 10（上限 90%）。\n更多折扣数值模式（0-90 任意整数）：非整十只改显示，实际结算按四舍五入整十倍数。\n本局立即生效，平衡板上可见；恢复原值可还原为 100%。", "Set your handicap to 100-discount %.\nSlider mode (0-90 in tens): 0 = off; 90 → handicap 10 (90% cap).\nMore-values mode (0-90 any integer): non-multiples only change display; tally rounds to the nearest ten.\nTakes effect immediately and shows on the balancer; Restore sets it back to 100%.")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.ApplyScoreDiscount();
            }
            GUILayout.Space(Sc(8));
            if (GUILayout.Button(new GUIContent(T("恢复原值", "Restore"), T("把自己的 handicap 恢复为 100%（清除折扣）", "Restore your handicap to 100% (clears the discount)")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.RestoreScoreDiscount();
            }
            GUILayout.Space(Sc(8));
            //更多折扣数值选择框（放到恢复原值右侧；悬浮说明直接挂在选择框 tooltip 上）
            ConfigEntryBase more = FindInternalEntry("实验", "More Discount Values");
            if (more != null) {
                string moreTip = T("更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n⚠ 游戏本身的 handicap 不支持任意百分比——内部按四舍五入取整十倍数（如 85 → 90、84 → 80）。开启后自由输入的非整十数值只修改显示，实际游戏结算仍按四舍五入后的整十倍数生效。",
                    "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).\n⚠ The game's handicap does not support arbitrary percentages - it rounds to the nearest multiple of 10 internally (e.g. 85 → 90, 84 → 80). Non-multiple values only change the display; the actual tally still uses the rounded multiple of 10.");
                bool nm = GUILayout.Toggle(moreOn, new GUIContent(T("更多折扣数值", "More values"), moreTip), GUILayout.Width(Sc(160)), GUILayout.Height(Sc(30)));
                if (nm != moreOn) SetValue(more, nm);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            GUILayout.Label(T("— 快速切换 —", "— Quick switch —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase qs = FindInternalEntry("实验", "Quick Switch");
            RestoreLabel(new GUIContent(T("快速切换", "Quick switch"), T("自由模式内按一下切换键（按下再松开）就在 行动↔建造 之间切换一次（长按不触发）。\n游戏默认：长按 B 键约 0.5 秒蓄力后切换。", "In freeplay, press and release the switch key to toggle play/build once (holding does nothing).\nGame default: hold B for ~0.5s to charge before switching.")), qs, Sc(140), Sc(52));
            if (qs != null) RenderControl(qs);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase qsk = FindInternalEntry("实验", "Quick Switch Key");
            RestoreLabel(new GUIContent(T("切换键", "Key"), T("快速切换键（默认 LeftCtrl）：自由模式内按下它立即切换行动/建造模式。\n游戏默认：长按 B 键约 0.5 秒蓄力后切换。", "The quick-switch key (default LeftCtrl): press it in freeplay to switch instantly.\nGame default: hold B for ~0.5s to charge.")), qsk, Sc(140), Sc(52));
            if (qsk != null) RenderControl(qsk);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase qst = FindInternalEntry("实验", "Quick Switch Time");
            RestoreLabel(new GUIContent(T("切换耗时", "Min hold"), T("最短按住时长（秒）：按下切换键后松开才切换。\n0 = 松开立即切换（默认）；大于 0 时需按住至少该时长再松开才切换（防误触）。", "Minimum hold time (seconds): the switch fires when the key is released.\n0 = switch on release instantly (default); >0 = must hold at least that long before releasing.")), qst, Sc(140), Sc(52));
            if (qst != null) RenderControl(qst);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            GUILayout.Label(T("— 快速自杀 —", "— Quick suicide —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase se = FindInternalEntry("Treehouse Suicide", "Enabled");
            RestoreLabel(new GUIContent(T("快速自杀", "Quick suicide"), T("总开关：开启后按 组合键（自杀键）快速自杀（树屋和局内都有效，只杀自己，默认 Shift+0）", "Master switch: press the suicide combo to die instantly (treehouse and in-match, only yourself, default Shift+0)")), se, Sc(140), Sc(52));
            if (se != null) RenderControl(se);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase sk = FindInternalEntry("Treehouse Suicide", "Keybind");
            RestoreLabel(new GUIContent(T("自杀键", "Suicide key"), T("自杀键（组合键）：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键即可设为组合键（默认 Shift+0）。\n树屋和局内都有效，只杀自己。", "Suicide key (combo): click the button then hold Shift/Ctrl/Alt while pressing the main key (default Shift+0).\nWorks in the treehouse and in-match; only kills yourself.")), sk, Sc(140), Sc(52));
            if (sk != null) RenderControl(sk);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.Space(Sc(6));
        }


        private static void RenderMapPage() {
            //地图总开关（顶部，独立于本 Mod 总开关；关闭后 M 键无法打开地图）
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                MapEnabled ? T("地图总开关：开", "Map Master: ON") : T("地图总开关：关", "Map Master: OFF"),
                MapEnabled ? _selItem : _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                SetValue(_mapEnabledEntry, !MapEnabled);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            WrapLabel(T("地图总开关：关闭后无法打开地图窗口（M 键无效），已打开的地图立即关闭。\n「地图网格」「同步循环」「树屋地图」等独立功能不受影响。",
                "Map master switch: OFF disables opening the map (M key does nothing); an open map closes immediately.\nIndependent features like Map grid / Sync cycles / Treehouse map are not affected."));
            GUILayout.Space(Sc(4));
            //说明标签
            WrapLabel(T("地图：打开后切换为自由相机俯视视角（M 键；「冻结角色」开启时自己的角色会停住，其他角色照常移动）。\nFOV 默认：树屋 5.2 / 自由模式等其他场景 10；滚轮缩放、鼠标左键拖拽平移。\nT 传送到鼠标位置（仅树屋/自由模式）；O 添加重生点并显示标记（仅自由模式）。\n可用范围：自由模式全功能；树屋大厅（需开启「树屋地图」）；挑战模式对局内禁用。",
                "Map: switches to the free-camera top-down view (M key; with Freeze Self ON your character stops while others keep moving).\nFOV defaults to: treehouse 5.2 / 10 elsewhere; wheel zooms, left-drag pans, T teleports to the cursor.\nFreeplay: O adds a spawn point at the cursor with markers.\nAvailable in: freeplay (full), treehouse lobby (requires Treehouse Map); disabled in Challenge matches."));
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            RestoreLabel(new GUIContent(T("地图按键", "Map key"), T("打开/关闭地图窗口的按键（默认 M）", "Key to open/close the map (default M)")), _mapKey, Sc(140), Sc(26));
            GUILayout.Space(Sc(4));
            RenderControl(_mapKey);
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //地图网格（原实验栏“网格常驻”，移到地图栏目）
            GUILayout.Label(T("— 地图网格 —", "— Map grid —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase ga = FindInternalEntry("实验", "Grid Always On");
            RestoreLabel(new GUIContent(T("地图网格", "Map grid"), T("行动状态下也显示建造网格（游戏默认只在建造阶段显示）。\n随开随关：开启立即淡入，关闭立即淡出；任何模式都生效。", "Keep the build grid visible during the play phase.\nOn/off takes effect immediately; works in every mode.")), ga, Sc(140), Sc(52));
            if (ga != null) RenderControl(ga);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //树屋地图（原实验栏，移到地图与关卡）
            GUILayout.Label(T("— 树屋地图 —", "— Treehouse map —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase tm = FindInternalEntry("实验", "Treehouse Map");
            RestoreLabel(new GUIContent(T("树屋地图", "Treehouse map"), T("在树屋大厅也能打开地图（M 键开）。\n左键拖拽平移，按 T 传送到鼠标位置。", "Open the map in the treehouse (M key). Drag to pan, press T to teleport to the cursor.")), tm, Sc(140), Sc(52));
            if (tm != null) RenderControl(tm);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //同步循环（强制发射器初始延迟统一 0.5 秒，不受 ping 影响）
            GUILayout.Label(T("— 同步循环 —", "— Sync cycles —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase sc = FindInternalEntry("地图", "同步循环");
            RestoreLabel(new GUIContent(T("同步循环", "Sync cycles"),
                T("强制所有发射器（炮弹/火焰等）的初始延迟统一为 0.5 秒。\n原版自由模式下延迟会减去网络延迟（0.5 - ping），高 ping 时发射节奏不稳定；开启后固定 0.5 秒，全员发射时机一致。", "Force every launcher (cannons/fire etc.) initial delay to a fixed 0.5s.\nVanilla freeplay subtracts ping (0.5 - ping), so high ping makes launch timing unstable; ON = fixed 0.5s for everyone.")),
                sc, Sc(140), Sc(52));
            if (sc != null) RenderControl(sc);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
        }

        //更多联机 page: master switch on top + player limit slider + options
        private static void RenderMorePlayersConsole() {
            GUILayout.Label(T("— 更多联机 —", "— More Online —"), _secHeader);
            //总开关
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                MorePlayers.Enabled ? T("更多联机：开", "More Online: ON") : T("更多联机：关", "More Online: OFF"),
                MorePlayers.Enabled ? _selItem : _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                ConfigEntryBase e = FindInternalEntry("更多玩家", "Enabled");
                if (e != null) SetValue(e, !MorePlayers.Enabled);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            WrapLabel(T("开启后主菜单点“更多联机”进入超过 4 人的联机房间（8-100 人；本地游戏/网络对战仍为原版 4 人）。\n“玩家上限”改动后点击主菜单“更多联机”按钮生效。\n主菜单点击“更多联机”按钮会修改版本信息以匹配不同的联机列表，导致游戏提示“请更新版本”，这是正常现象，确保 UCH 在 Steam 等平台已更新至最新版即可。",
                "When ON, press More on the main menu to host/join >4 player online rooms (8-100; Local/Online stay vanilla 4).\nChanging the limit takes effect after pressing the More button.\nPressing More on the main menu rewrites the version info to match the modded lobby list, so the game may show a \"Please update\" prompt — that is normal; just make sure UCH is updated to the latest version on Steam etc."));
            GUILayout.Space(Sc(4));

            ConfigEntryBase lim = FindInternalEntry("更多玩家", "玩家上限");
            if (lim != null) {
                GUILayout.BeginHorizontal();
                RestoreLabel(new GUIContent(T("玩家上限", "Player limit"), T("最多允许的玩家数（2 - 100；游戏原版为 4）", "Max players (2-100; vanilla is 4)")), lim, Sc(140), Sc(26));
                RenderControl(lim);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(2));
            }
            ConfigEntryBase fd = FindInternalEntry("更多玩家", "完整调试");
            if (fd != null) {
                GUILayout.BeginHorizontal();
                RestoreLabel(new GUIContent(T("完整调试", "Full debug"), T("输出更多调试日志（排查问题时再开）", "More debug logs (only when troubleshooting)")), fd, Sc(140), Sc(26));
                RenderControl(fd);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(2));
            }
            ConfigEntryBase ssb = FindInternalEntry("更多玩家", "平衡板置顶");
            if (ssb != null) {
                GUILayout.BeginHorizontal();
                RestoreLabel(new GUIContent(T("平衡板置顶", "Score balancer on top"), T("树屋平衡板上把最后修改的玩家显示在最上面", "Show the player who last edited the score balancer on top")), ssb, Sc(140), Sc(26));
                RenderControl(ssb);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(2));
            }
            GUILayout.Space(Sc(4));
            WrapLabel(T("提示：主菜单四个按钮 = 本地游戏（原版 4 人）/ 网络对战（原版联机）/ 更多联机（8-100 人）/ 模组联机（原版 4 人、只显示装了本 mod 的房间）。\n“更多联机”只影响联机房间，本地游戏不受影响；联机建议所有参与者使用相同配置。",
                "Tip: main menu = Local (vanilla 4) / Online (vanilla) / More (8-100 players) / Mod Lobby (vanilla 4, mod-only rooms).\nMore affects online rooms only; local games stay vanilla. Online players should share the same config."));
            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(4));
        }

        //模组联机 page: master switch + description (source is ModMC.cs)
        private static void RenderModMCConsole() {
            GUILayout.Label(T("— 模组联机 —", "— Mod Lobby —"), _secHeader);
            //总开关
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                ModMC.Enabled ? T("模组联机：开", "Mod Lobby: ON") : T("模组联机：关", "Mod Lobby: OFF"),
                ModMC.Enabled ? _selItem : _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                ConfigEntryBase e = FindInternalEntry("模组联机", "Enabled");
                if (e != null) SetValue(e, !ModMC.Enabled);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            WrapLabel(T("列表按版本前缀 usingMods 过滤：只有房主也点过“模组联机”的房间才会出现。\n邀请码 5 位、第一位是 R（对应更多联机的 M 码机制），与普通房间码互不相通。\n加入模组联机房间会自动识别；本地游戏/网络对战仍为原版 4 人。",
                "The list is filtered by the usingMods version prefix: only rooms whose host also pressed Mod Lobby show up.\nInvite codes are 5 chars starting with R (mirroring More's M codes) and are separate from vanilla room codes.\nJoining a mod lobby auto-detects; Local/Online stay vanilla 4."));
            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(4));
        }

        //尝试计数 page: master switch + status + toggle key + clear + usage
        private static void RenderAttemptCounterConsole() {
            GUILayout.Label(T("— 尝试计数 —", "— Attempt Counter —"), _secHeader);
            //总开关
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                AttemptCounter.Enabled ? T("尝试计数：开", "Attempt Counter: ON") : T("尝试计数：关", "Attempt Counter: OFF"),
                AttemptCounter.Enabled ? _selItem : _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                ConfigEntryBase e = FindInternalEntry("尝试计数", "Enabled");
                if (e != null) SetValue(e, !AttemptCounter.Enabled);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));

            //状态区：当前显示 + 统计
            GUILayout.BeginVertical(_footer);
            GUILayout.Label(T("当前显示：", "Now showing: ") + (AttemptCounter.RuntimeOn ? T("开（游戏内按开关按键隐藏）", "ON (press the toggle key to hide)") : T("关（游戏内按开关按键恢复）", "OFF (press the toggle key to restore)")), _label, GUILayout.Height(Sc(26)));
            GUILayout.Label(T("已记录关卡: ", "Levels tracked: ") + AttemptCounter.RecordedLevels
                + T("    累计尝试: ", "    Total attempts: ") + AttemptCounter.TotalAttempts, _label, GUILayout.Height(Sc(26)));
            GUILayout.EndVertical();
            GUILayout.Space(Sc(4));

            //开关按键
            ConfigEntryBase key = FindInternalEntry("尝试计数", "Toggle Key");
            if (key != null) {
                GUILayout.BeginHorizontal();
                RestoreLabel(new GUIContent(T("开关按键", "Toggle key"), T("游戏内切换尝试计数显示/隐藏", "Toggle the counter display in-game")), key, Sc(140), Sc(26));
                RenderControl(key);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(2));
            }

            //清空记录
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("清空全部记录", "Clear all"), T("删除所有关卡的尝试次数记录（不可恢复）", "Delete every attempt record (cannot be undone)")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                AttemptCounter.ClearAll();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //使用说明
            GUILayout.Label(T("— 使用说明 —", "— How it works —"), _secHeader);
            WrapLabel(T("· 挑战模式：每失败/重试一次 = 1 次尝试\n· 自由模式：每死亡重置一次 = 1 次尝试（只统计你自己）\n· 在树屋选关时，信息面板会显示“我的尝试次数”（金色加粗）\n· 数据保存在本地配置文件，换设备/重装后不会自动迁移",
                "· Challenge: each retry/fail counts as 1 attempt\n· Freeplay: each death-reset counts as 1 attempt (yours only)\n· The level-select pane shows My Attempts in gold bold\n· Stored in a local file; not migrated between devices"));
            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(4));
        }

        //附加模块页：master switch on top, a status box in the middle, action buttons below
        //（EX 附加页始终中文显示，不受界面语言切换影响）
        private static void RenderCultivationConsole() {
            bool oldForce = _forceZh;
            _forceZh = true; //EX 页豁免：强制中文
            try {
            //master switch
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                ExRef.Enabled ? T("EX总开关：开", "EX master: ON") : T("EX总开关：关", "EX master: OFF"),
                ExRef.Enabled ? _selItem : _btn, GUILayout.Width(Sc(160)), GUILayout.Height(Sc(30)))) {
                ExRef.Enabled = !ExRef.Enabled;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            //status box: player table (number | animal | score) with header + hint lines
            GUILayout.BeginVertical(_footer);
            GUILayout.Label(T("目标: ", "Target: ") + ExRef.TargetName()
                + T("   （点击表格中的复选框选择目标）", "   (click a checkbox to pick a target)"), _label, GUILayout.Height(Sc(26)));
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("号数", "#"), _label, GUILayout.Width(Sc(56)), GUILayout.Height(Sc(26)));
            GUILayout.Label(T("角色", "Animal"), _label, GUILayout.Width(Sc(110)), GUILayout.Height(Sc(26)));
            GUILayout.Label(T("评分", "Score"), _label, GUILayout.Width(Sc(70)), GUILayout.Height(Sc(26)));
            GUILayout.EndHorizontal();
            int curNum = ExRef.CurrentTargetNumber();
            int rowIdx = 0;
            foreach (var row in ExRef.PlayerTable()) {
                int idx = rowIdx;
                int rnum = ExRef.RowNumber(row);
                bool isSel = rnum == curNum;
                bool isSelf = ExRef.IsSelf(rnum);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(isSel ? "✓" : "", isSel ? _checkOn : _checkOff, GUILayout.Width(Sc(28)), GUILayout.Height(Sc(26)))) {
                    ExRef.SelectTargetByIndex(idx);
                }
                GUILayout.Label(rnum.ToString(), _label, GUILayout.Width(Sc(46)), GUILayout.Height(Sc(26)));
                GUILayout.Label(ExRef.RowAnimal(row) + (isSelf ? T("（我）", " (me)") : ""), _label, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(26)));
                GUILayout.Label(ExRef.RowScore(row).ToString(), _label, GUILayout.Width(Sc(70)), GUILayout.Height(Sc(26)));
                GUILayout.EndHorizontal();
                rowIdx++;
            }
            GUILayout.Label(T("加分/加金币/踢人 作用于目标", "score/coin/kick affect the target"), _labelWrap);
            string posLine = ExRef.Positions();
            if (posLine.Length > 0) {
                GUILayout.Label(T("坐标: ", "Pos: ") + posLine, _labelWrap);
            }
            string loadState = ExRef.LoadingState();
            if (loadState.Length > 0) {
                GUILayout.Label(loadState, _labelWrap);
            }
            GUILayout.EndVertical();
            GUILayout.Space(Sc(8));

            //action buttons (grayed out while the master switch is off)
            bool oldEn = GUI.enabled;
            GUI.enabled = ExRef.Enabled;
            float bw = Sc(118), bh = Sc(30);
            if (!ExRef.Enabled) {
                GUILayout.Label(T("⚠ EX总开关未开启：下方操作按钮、下拉框与编辑框均不可用，请先点上方“EX总开关：关”开启（同时需打开总开关）",
                    "⚠ EX master switch is OFF: action buttons, dropdowns and edit boxes below are disabled - turn it ON above (and the master switch)"), _labelWrap);
                GUILayout.Space(Sc(2));
            }
            GUILayout.Label(T("— 操作 —", "— Actions —"), _secHeader);
            //踢人 / 解散对局（都是全员：伪造游戏原生消息，房主原生转发）
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("踢出目标", "Kick"), T("把目标踢出房间（房主直接踢，房客伪造消息，全员生效）", "Kick the target (host direct / guest forged, affects everyone)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.KickTarget();
            if (GUILayout.Button(new GUIContent(T("解散对局", "Disband"), T("全员解散回主菜单（伪造 HostEndedGame，房主原生转发；结算卡住或结束后解困）", "Disband the match to the main menu (forge HostEndedGame; unstuck when stuck)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.DisbandMatch();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //score: type dropdown + apply（游戏原生 PointAwarded 全员；分值按类型标准值：获胜 50、陷阱 10 等）
            GUILayout.BeginHorizontal();
            bool enCtl = GUI.enabled;
            if (ExRef.ScoreTypeEntry != null) {
                RenderControl(ExRef.ScoreTypeEntry);
            }
            GUI.enabled = enCtl;
            if (GUILayout.Button(new GUIContent(T("加分", "Score"), T("给目标按类型标准分值加分（获胜 50 / 陷阱 10 等）：走游戏原生消息，全员计分板可见分块", "Award points by the type's standard value (win 50 / trap 10 etc.): native message, everyone sees a block")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.AddScore();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //coin amount + button（游戏原生 PointAwarded(coin) 全员）
            GUILayout.BeginHorizontal();
            if (ExRef.CoinAmountEntry != null) {
                RenderControl(ExRef.CoinAmountEntry);
            }
            GUI.enabled = enCtl;
            if (GUILayout.Button(new GUIContent(T("加金币", "Coin"), T("给目标加金币：游戏原生消息全员计分板显示金币分块 + 本地金币计数", "Add coins: native message shows a coin block on everyone's scoreboard + local count")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.AddCoin();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //受限功能：送达终点 / 复活 / 加生命 / 指定关卡
            GUILayout.Space(Sc(2));
            GUILayout.Label(T("— 受限功能 —", "— Restricted —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("杀死目标", "Kill"), T("让自己死亡（本地方法经服务器广播，仅对自己生效，全员可见死亡；force 绕过无敌）", "Kill yourself (local method broadcast via server, self only, everyone sees the death; force bypasses invincibility)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.KillTarget();
            if (GUILayout.Button(new GUIContent(T("复活", "Respawn"), T("让自己重生回起点（Command 经服务器，仅对自己生效）", "Respawn yourself (Command via server, self only)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.RespawnTarget();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("送达终点", "Win"), T("让自己到达终点获胜（Command 经服务器，仅对自己生效，全员可见获胜）", "Win as yourself (Command via server, self only, everyone sees the win)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.WinTarget();
            if (GUILayout.Button(new GUIContent(T("结束回合", "End round"), T("当前回合立即进入结算（游戏原生 StartPhaseEvent：房主广播全员结算；需服务器权限：仅单机/本地派对/房主有效）", "End the current round into scoring (native StartPhaseEvent: host broadcasts to everyone; needs server authority: solo/local party/host only)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.EndRound();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            if (ExRef.LivesAmountEntry != null) {
                RenderControl(ExRef.LivesAmountEntry);
            }
            GUI.enabled = enCtl;
            if (GUILayout.Button(new GUIContent(T("加生命", "Lives"), T("改自己剩余生命（lives 非同步字段：仅自己本地显示，单机/本地派对才真正生效）", "Change your lives (not synced: self/local only, real in solo/local party)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.AddLives();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //指定关卡放加生命下面（需服务器权限：仅单机/本地派对/房主有效）
            GUILayout.BeginHorizontal();
            if (ExRef.TargetLevelEntry != null) {
                RenderControl(ExRef.TargetLevelEntry);
            }
            GUI.enabled = enCtl;
            if (GUILayout.Button(new GUIContent(T("指定关卡", "Force level"), T("树屋大厅直接开始所选关卡（需服务器权限：仅单机/本地派对/房主有效）", "Start the chosen level (needs server authority: solo/local party/host only)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.ForceLevel();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //自身状态（本地生效）
            GUILayout.Space(Sc(2));
            GUILayout.Label(T("— 自身状态 —", "— Self states —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("无敌：", "Invincible: ") + (ExRef.InvincibleOn ? T("开", "ON") : T("关", "OFF")), T("自己免疫非强制死亡", "You are immune to non-forced deaths")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.ToggleInvincible();
            if (GUILayout.Button(new GUIContent(T("飞天：", "Fly: ") + (ExRef.FlyOn ? T("开", "ON") : T("关", "OFF")), T("方向键自由飞行（不按悬浮）", "Fly with the arrow keys")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.ToggleFly();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("蹲移：", "Duck: ") + (ExRef.CrouchMoveOn ? T("开", "ON") : T("关", "OFF")), T("保持蹲下自由移动", "Stay ducked and move freely")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.ToggleCrouchMove();
            if (GUILayout.Button(new GUIContent(T("防踢：", "Anti-kick: ") + (ExRef.AntiKickOn ? T("开", "ON") : T("关", "OFF")), T("免疫被踢出", "Never be kicked")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.ToggleAntiKick();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUI.enabled = oldEn;
            GUILayout.Space(Sc(6));
            //三个复选框开关放一起：允许客户端删除 / 无视模式限制 / 无视房主限制
            GUILayout.BeginHorizontal();
            ConfigEntryBase ac = FindInternalEntry("EX", "Allow Clients");
            RestoreLabel(new GUIContent(T("允许客户端删除", "Allow clients delete"), T("非房主玩家也能删除方块（由房主同步）", "Non-host players can destroy blocks")), ac, Sc(140), Sc(26));
            if (GUILayout.Button(DestroyBlocks.AllowClientsOn ? "✓" : "", DestroyBlocks.AllowClientsOn ? _checkOn : _checkOff, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) DestroyBlocks.ToggleAllowClients();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //ignore-mode-limit switch (independent of the master switch): unlock every
            //FREEPLAY-only feature (视野/地图/重生/附加功能) in any game mode
            GUILayout.BeginHorizontal();
            RestoreLabel(new GUIContent(T("无视模式限制", "Ignore mode limit"), T("视野/地图/重生/附加功能等在任何游戏模式下都可用", "Unlock every mode-limited feature in any game mode")), _ignoreModeLimitEntry, Sc(140), Sc(26));
            if (GUILayout.Button(IgnoreModeLimit ? "✓" : "", IgnoreModeLimit ? _checkOn : _checkOff, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) {
                IgnoreModeLimit = !IgnoreModeLimit;
                if (_ignoreModeLimitEntry != null) _ignoreModeLimitEntry.Value = IgnoreModeLimit;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //无视房主限制：开启后房客也能执行房主限制的操作（如树屋问号添加/删除等）
            GUILayout.BeginHorizontal();
            ConfigEntryBase ih = FindInternalEntry("EX", "Ignore Host Limit");
            RestoreLabel(new GUIContent(T("无视房主限制", "Ignore host limit"), T("开启后房客也能执行房主限制的操作（如树屋问号添加/删除等）", "When on, guests can use host-only operations (e.g. treehouse question marks)")), ih, Sc(140), Sc(26));
            if (GUILayout.Button(ExRef.IgnoreHostLimit ? "✓" : "", ExRef.IgnoreHostLimit ? _checkOn : _checkOff, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) {
                ExRef.IgnoreHostLimit = !ExRef.IgnoreHostLimit;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //冻结角色：打开面板/地图时只冻结自己（默认关 = 游戏照常运行、自己也能动）
            GUILayout.BeginHorizontal();
            ConfigEntryBase pg = FindInternalEntry("EX", "Freeze Character");
            RestoreLabel(new GUIContent(T("冻结角色", "Freeze self"), T("打开面板/地图时冻结自己的角色，其他角色照常移动（默认关闭：打开面板/地图时游戏照常运行、自己也能动）", "Freeze your own character while the panel/map is open; other characters keep moving (OFF by default: game keeps running and you can move)")), pg, Sc(140), Sc(26));
            if (GUILayout.Button(PauseGame ? "✓" : "", PauseGame ? _checkOn : _checkOff, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) {
                PauseGame = !PauseGame;
                if (_freezeCharEntry != null) _freezeCharEntry.Value = PauseGame;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            } finally {
                _forceZh = oldForce;
            }
        }

        //首页: overview + links to the separate mod parts
        private static void RenderHomePage() {
            GUILayout.Label(T("— 欢迎使用 SR＿UCH —", "— Welcome to SR＿UCH —"), _secHeader);
            WrapLabel(T("Ultimate Chicken Horse 模组整合增强包（免费开源）。", "A free open-source enhancement pack for Ultimate Chicken Horse."));
            WrapLabel(T("本 mod 参考了 BetterFreeplay，BetterNight，BuildingPlus，BuildUnlimiter，Even More Players，UCH Freeplay Spawn Setter，UCH Tweaks，UCH-PlayerTracker-Mod，UltimateBuilder，向这些 mod 的制作者表示感谢。", "This mod references BetterFreeplay, BetterNight, BuildingPlus, BuildUnlimiter, Even More Players, UCH Freeplay Spawn Setter, UCH Tweaks, UCH-PlayerTracker-Mod and UltimateBuilder. Thanks to their authors."));
            GUILayout.Space(Sc(4));

            //开源地址：点击用浏览器打开 GitHub 仓库
            GUILayout.Label(T("— 开源地址 —", "— Source —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("https://github.com/RSTFS/SR_UCH", _btn, GUILayout.Height(Sc(30)))) {
                try { Application.OpenURL("https://github.com/RSTFS/SR_UCH"); } catch { }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            GUILayout.Space(Sc(8));
            GUILayout.Label(T("— 使用提示 —", "— Tips —"), _secHeader);
            WrapLabel(T("· 修改配置后点底部“保存”写盘；“重新加载”放弃本次修改。", "· Use Save to write config, Reload to discard."));
            WrapLabel(T("· 自定义按键：点按键按钮后，直接按一个键设为单键；按住 Shift/Ctrl/Alt 再按主键设为组合键（如 Shift+P）。Esc 清空，Shift+Esc 取消。", "· Custom keys: click the key button, then press a key for single-key; hold Shift/Ctrl/Alt while pressing a key for a combo (e.g. Shift+P). Esc clears, Shift+Esc cancels."));
            WrapLabel(T("· 通用条目页：点击条目前的名称即可恢复默认值。", "· Generic entries: click the name to restore its default."));
            GUILayout.Space(Sc(4));

            GUILayout.Label(T("— 请共同维护游戏体验 —", "— Keep the game fun for everyone —"), _secHeader);
            WrapLabel(T("请不要使用本模组破坏别人的游戏体验。", "Please do not use this mod to ruin other players' experience."));
            GUILayout.Space(Sc(4));

            GUILayout.FlexibleSpace();
            GUILayout.Space(Sc(4));
        }

        //换行标签：按实际可用宽度测量高度，保证最后一行文字不被裁切
        private static void WrapLabel(string text) {
            float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
            GUILayout.Label(text, _labelWrap, GUILayout.Width(w), GUILayout.Height(TextHeight(text, w)));
        }

        //附加页: exploration features with an apply button for the score discount
        private static void RenderExperimentsConsole() {
            //实验区总警告（黄色，醒目）
            GUIStyle warn = new GUIStyle(_labelWrap);
            warn.normal.textColor = new Color(1f, 0.85f, 0.3f, 1f);
            GUILayout.Label(T("⚠ 实验区的功能处于测试阶段，可能会导致游戏稳定性下降以及更多的 bug。",
                "⚠ Experimental features are in testing; they may reduce stability and cause more bugs."), warn);
            GUILayout.Space(Sc(4));
            GUILayout.Label(T("— 位置同步 —", "— Position sync —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase no = FindInternalEntry("实验", "Net Optimize");
            RestoreLabel(new GUIContent(T("位置同步", "Position sync"),
                T("按设定频率主动上报自己的位置，让其他玩家看到你的移动更平滑更跟手。\n原理：游戏默认只在关键事件时同步位置，开启后按固定频率持续上报。\n仅对局内生效；本地派对/单机无网络时无效果。", "Actively report your position at a fixed rate so others see smoother movement.\nThe game normally syncs only on key events; this pushes it every tick.\nIn-match only; no effect in local party / single player.")),
                no, Sc(140), Sc(52));
            if (no != null) RenderControl(no);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            ConfigEntryBase hz = FindInternalEntry("实验", "Sync Frequency");
            if (hz != null) {
                GUILayout.BeginHorizontal();
                RestoreLabel(new GUIContent(T("同步频率", "Rate"),
                    T("同步频率（10 - 50 Hz，默认 20）：每秒钟上报多少次位置。\n越高 → 其他玩家看到的你越平滑跟手，但占用更多带宽与 CPU；\n越低 → 更省流量，但对方看到的移动可能一顿一顿。联机延迟高时建议调低。", "Sync rate (10-50 Hz, default 20): position reports per second.\nHigher = smoother for others but more bandwidth/CPU; lower = lighter but choppier. Lower it on high-ping connections.")),
                    hz, Sc(140), Sc(52));
                RenderControl(hz);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(Sc(2));
            }
            GUILayout.BeginHorizontal();
            ConfigEntryBase sa = FindInternalEntry("实验", "Sync All");
            RestoreLabel(new GUIContent(T("同步范围", "Scope"),
                T("开 = 把所有玩家的位置都按频率同步（需房主权限，适合低延迟局域网/本地派对联机）；\n关 = 只同步你自己（默认，推荐，其他玩家由各自客户端上报）。", "ON = sync EVERY player's position (host only, best for low-latency LAN);\nOFF = sync only yourself (default; others report their own positions).")),
                sa, Sc(140), Sc(52));
            if (sa != null) RenderControl(sa);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            //加载后清理（独立分区：进关卡/换关卡时 GC 回收 + 资源卸载；同关卡回合切换不清理）
            GUILayout.Label(T("— 加载后清理 —", "— Cleanup after load —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase gc = FindInternalEntry("地图", "加载后清理");
            RestoreLabel(new GUIContent(T("加载后清理", "GC after load"),
                T("进关卡/换关卡时执行一次 GC 回收 + 资源卸载，减少对局内卡顿。\n同关卡回合切换不清理（场景名不变自动跳过），不影响结算速度。",
                  "Runs GC + asset unload once on level load (skipped on same-level round reloads), reducing in-match stutter.")),
                gc, Sc(140), Sc(52));
            if (gc != null) RenderControl(gc);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            //树屋问号（仅房主可操作；四个按钮都受“树屋问号”总开关控制）
            GUILayout.Label(T("— 树屋问号 —", "— Treehouse question mark —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase qm = FindInternalEntry("实验", "Question Mark");
            RestoreLabel(new GUIContent(T("树屋问号", "Question mark"), T("给指定关卡的门添加问号（未解锁的关卡不能添加）。\n仅房主可操作；四个按钮都受“树屋问号”总开关控制。", "Add a question mark to a level's portal (locked levels can't).\nHost-only; all four buttons are gated by the master switch.")), qm, Sc(140), Sc(52));
            if (qm != null) RenderControl(qm);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase ql = FindInternalEntry("实验", "Question Level");
            RestoreLabel(new GUIContent(T("问号关卡", "Level"), T("要添加问号的关卡", "Which level to mark")), ql, Sc(140), Sc(26));
            if (ql != null) RenderControl(ql);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("添加问号", "Add ?"), T("给上方选中的关卡添加问号（进入该地图有解锁盒子）", "Mark the selected level with a question mark")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.ApplyQuestionMark();
            }
            if (GUILayout.Button(new GUIContent(T("删除问号", "Remove ?"), T("删除上方选中的关卡的门上的问号", "Remove the question mark on the selected level")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.RemoveQuestionMark();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("全部添加问号", "Add all ?"), T("为当前所有已解锁的关卡添加问号（未解锁的自动跳过）", "Add a question mark to every unlocked level")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.AddAllQuestionMarks();
            }
            if (GUILayout.Button(new GUIContent(T("一键清除全部问号", "Clear all ?"), T("清空树屋里所有关卡门上的问号", "Clear every question mark in the treehouse")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.ClearQuestionMarks();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //广播方块快照（独立分区；仅房主有效：房主重发关卡快照，全员重建方块，修复方块消失/不同步）
            GUILayout.Label(T("— 广播方块快照 —", "— Broadcast snapshot —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("广播方块快照", "Broadcast snapshot"),
                T("房主把当前视角的所有方块（含位置/旋转/属性）打包广播 → 全员按房主视角重建方块。\n用于修复偶发的“局内方块在自己视野消失/不同步”（同步 bug 导致的本地方块状态错乱）。\n不重载场景：对局进度与分数保留，全员短暂卡顿后方块即重建。\n⚠ 仅派对/创意局内生效；仅房主有效：需服务器权限（快照压缩广播）；房主未装本模组时不可用，房客请房主操作。", "Host packages all blocks in their view (position/rotation/properties) and broadcasts → everyone rebuilds blocks from the host snapshot.\nFixes occasional in-match blocks disappearing/desyncing (local state corruption).\nNo scene reload: match progress and scores are kept; blocks rebuild after a brief hitch.\n⚠ Party/Creative matches only; host only: needs server authority (snapshot broadcast); requires the host to have this mod.")),
                _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                Experiments.BroadcastSnapshot();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //重载关卡（独立分区；仅房主有效：真正重载场景，方块靠重载前写入 levelPortalXml 保留）
            GUILayout.Label(T("— 重载关卡 —", "— Reload level —"), _secHeader);
            //重载模式：保留方块和分数（允许补分） / 仅保留方块（跳过补分）
            GUILayout.BeginHorizontal();
            ConfigEntryBase rlm = FindInternalEntry("实验", "Reload Mode");
            RestoreLabel(new GUIContent(T("重载模式", "Reload mode"),
                T("重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的**分类型分块**（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks are kept; the host broadcasts a fill using the pre-reload **per-type blocks** (win/coin/trap etc. as-is), so everyone (incl. clients without the mod) sees the same score and types as before at the next round's tally (fill does not trigger an immediate tally; icons appear with the normal round end).\nKeep blocks only (skip fill) = blocks are kept, score resets (fresh match, no fill).")),
                rlm, Sc(140), Sc(52));
            if (rlm != null) RenderControl(rlm);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("重载关卡", "Reload level"),
                T("房主把当前视角的所有方块写入关卡快照 → **真正重载当前关卡场景**（全员重新加载）。\n重载后房主按快照重建方块并广播：所有方块（含玩家放置的、可移动的）不丢；\n按上方“重载模式”决定是否补分保留分数。\n⚠ 仅派对/创意局内生效；仅房主有效：需服务器权限（场景重载）；房主未装本模组时不可用，房客请房主操作。", "Host writes the current blocks into the level snapshot → **truly reloads the current level scene** (everyone reloads).\nAfter reload the host rebuilds blocks from the snapshot and broadcasts: every block (including player-placed, movable ones) is kept;\nwhether scores are filled depends on the Reload mode above.\n⚠ Party/Creative matches only; host only: needs server authority (scene reload); requires the host to have this mod.")),
                _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                Experiments.ReloadLevel();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            GUILayout.Label(T("— 读取统计 —", "— Read stats —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("刷新统计", "Refresh"), T("读取主用户的存档统计（对局/时长/奔跑长度等）", "Read the main user's save stats")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                _statTextCache = Experiments.ReadStatsText();
                _statsAutoTimer = 0f;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //页面打开时每秒自动刷新，局内能看到实时累计（奔跑长度/金币/死亡等）
            _statsAutoTimer += Time.unscaledDeltaTime;
            //切换语言后强制刷新（缓存文本语言与当前不一致）
            if (_cacheLangEn != _langEn) {
                _cacheLangEn = _langEn;
                _statTextCache = "";
                _cheatFlagText = "";
            }
            if (_statsAutoTimer >= 1f) {
                _statsAutoTimer = 0f;
                _statTextCache = Experiments.ReadStatsText();
            }
            if (_statTextCache == null || _statTextCache.Length == 0) {
                _statTextCache = Experiments.ReadStatsText();
            }
            WrapLabel(_statTextCache);
            GUILayout.Label(T("（对局结束后结算；奔跑长度/金币/死亡等在对局中实时累计）",
                "(settled after a match ends; distance/coins/deaths accumulate live in-match)"), _label);
            GUILayout.Space(Sc(4));
            //进度解锁状态：两种独立进度——
            //A 组（游戏时长>17h16m18s 或 奔跑长度>52000m）：建造增强（无视碰撞）
            //B 组（游戏时长>52h 或 奔跑长度>100000m）：方块破坏、自身增益
            //达标前对应组保持禁用；达标后解锁可手动开启
            GUILayout.Label(T("— 功能解锁 —", "— Progression unlock —"), _secHeader);
            WrapLabel(Experiments.ProgressionText());
            GUILayout.Space(Sc(6));

            GUILayout.Label(T("— 作弊标识 —", "— Cheat flag —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("刷新标识", "Refresh flag"), T("读取当前存档是否被标记为作弊", "Read whether this save is flagged as a cheater")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                _cheatFlagText = Experiments.CheatFlagText();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            if (_cheatFlagText == null || _cheatFlagText.Length == 0) {
                _cheatFlagText = Experiments.CheatFlagText();
            }
            WrapLabel(_cheatFlagText);
            GUILayout.Space(Sc(4));

            GUILayout.Label(T("— 自身增益（仅自由模式）—", "— Self buffs (freeplay only) —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase si = FindInternalEntry("实验", "Self Invincible");
            RestoreLabel(new GUIContent(T("自身无敌", "Invincible"), T("免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）。\n仅自由模式有效；只作用于自己。", "Immune to all non-forced deaths (traps/bullets/pits/punches).\nFreeplay only; affects only you.")), si, Sc(140), Sc(52));
            if (si != null) RenderControl(si);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase sf = FindInternalEntry("实验", "Self Fly");
            RestoreLabel(new GUIContent(T("自身飞天", "Fly"), T("方向键自由飞行（上/下升降，左/右平移，按住 Shift 加速，不按键则悬浮）。\n仅自由模式有效；只作用于自己。", "Fly with the arrow keys (up/down, left/right, Shift to sprint, hover when idle).\nFreeplay only; affects only you.")), sf, Sc(140), Sc(52));
            if (sf != null) RenderControl(sf);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase scm = FindInternalEntry("实验", "Self Crouch Move");
            RestoreLabel(new GUIContent(T("自身蹲移", "Crouch move"), T("蹲下时也能左右移动（A/D 或方向键）。\n仅自由模式有效；只作用于自己。", "Move left/right while crouching (A/D or arrows).\nFreeplay only; affects only you.")), scm, Sc(140), Sc(52));
            if (scm != null) RenderControl(scm);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //角色声音（独立分区：关闭自己的/其它玩家的角色声音）
            GUILayout.Label(T("— 角色声音 —", "— Character sound —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase mown = FindInternalEntry("实验", "Mute Own");
            RestoreLabel(new GUIContent(T("关闭自己的声音", "Mute own"),
                T("静音自己角色的角色音效（走路/跳跃/落地/掉落等，由角色发出的声音）。\n只影响自己，其他人不受影响。", "Mutes your own character's sounds (walk/jump/land/fall etc. emitted by the character).\nOnly affects you; others are unaffected.")),
                mown, Sc(140), Sc(52));
            if (mown != null) RenderControl(mown);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase moth = FindInternalEntry("实验", "Mute Others");
            RestoreLabel(new GUIContent(T("关闭其它玩家的声音", "Mute others"),
                T("静音其它玩家角色的角色音效（自己听不到，不影响对方）。\n对方自己的客户端不受影响。", "Mutes other players' character sounds (you won't hear them; their clients are unaffected).")),
                moth, Sc(140), Sc(52));
            if (moth != null) RenderControl(moth);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
        }

        //the 会话内容 page: 顶部工具行（清空 / 过滤快捷消息 / 显示具体时间）+ 大型编辑框显示全部记录
        private static void RenderChatLog() {
            //顶部工具行
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("清空", "Clear"), _btn, GUILayout.Width(Sc(56)), GUILayout.Height(Sc(26)))) {
                ChatLog.Clear();
                _chatTextCache = null; //清空后立即刷新
            }
            GUILayout.Space(Sc(8));
            if (GUILayout.Button(_chatFilterQuick ? "✓" : "", _chatFilterQuick ? _checkOn : _checkOff, GUILayout.Width(Sc(26)), GUILayout.Height(Sc(26)))) {
                _chatFilterQuick = !_chatFilterQuick;
                if (_chatFilterQuickEntry != null) _chatFilterQuickEntry.Value = _chatFilterQuick; //配置持久化
            }
            GUILayout.Label(T("过滤快捷消息", "Hide quick msgs"), _label, GUILayout.Height(Sc(26)));
            GUILayout.Space(Sc(16));
            if (GUILayout.Button(_chatShowTime ? "✓" : "", _chatShowTime ? _checkOn : _checkOff, GUILayout.Width(Sc(26)), GUILayout.Height(Sc(26)))) {
                _chatShowTime = !_chatShowTime;
            }
            GUILayout.Label(T("显示具体时间", "Show time"), _label, GUILayout.Height(Sc(26)));
            GUILayout.Space(Sc(16));
            if (GUILayout.Button(_hideChat ? "✓" : "", _hideChat ? _checkOn : _checkOff, GUILayout.Width(Sc(26)), GUILayout.Height(Sc(26)))) {
                _hideChat = !_hideChat;
                if (_hideChatEntry != null) _hideChatEntry.Value = _hideChat; //配置持久化
            }
            GUILayout.Label(T("隐藏聊天窗口", "Hide chat window"), _label, GUILayout.Height(Sc(26)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            //大型编辑框（自动换行、内部滚动）显示全部记录
            //每秒重建一次内容（跟随新消息），避免每帧拼接字符串
            List<ChatLog.ChatEntry> entries = ChatLog.Entries;
            _chatTextTimer -= Time.unscaledDeltaTime;
            if (_chatTextTimer <= 0f || _chatTextCache == null
                || _chatCacheFilter != _chatFilterQuick || _chatCacheShowTime != _chatShowTime) {
                _chatTextTimer = 1f;
                _chatCacheFilter = _chatFilterQuick;
                _chatCacheShowTime = _chatShowTime;
                _chatTextCache = BuildChatText(entries);
            }
            if (entries.Count == 0) {
                GUILayout.Label(T("（暂无消息）", "(no messages)"), _label);
            } else if (string.IsNullOrEmpty(_chatTextCache)) {
                GUILayout.Label(T("（已过滤全部消息）", "(all messages filtered)"), _label);
            } else {
                float h = Mathf.Max(Sc(200), _winHeight - Sc(150));
                GUILayout.TextArea(_chatTextCache, _chatLabel, GUILayout.Height(h));
            }
        }

        //拼接会话记录文本（按过滤/时间开关），全部被过滤时返回 null
        private static string BuildChatText(List<ChatLog.ChatEntry> entries) {
            StringBuilder sb = new StringBuilder();
            bool any = false;
            for (int i = 0; i < entries.Count; i++) {
                ChatLog.ChatEntry ce = entries[i];
                if (_chatFilterQuick && ce.isQuick) continue;
                any = true;
                if (_chatShowTime) {
                    sb.Append(ce.time).Append(' ');
                }
                sb.Append('<').Append(ce.sender).Append("> ").Append(ce.text).Append('\n');
            }
            return any ? sb.ToString() : null;
        }

        //hover tooltip box (drawn last so it stays on top)
        private static void DrawTooltip(Vector2 mp) {
            string tip = GUI.tooltip;
            if (tip == null || tip.Length == 0) return;
            GUIContent content = new GUIContent(tip);
            float maxW = Mathf.Min(380f, Screen.width * 0.5f);
            float h = _tooltip.CalcHeight(content, maxW);
            float w = Mathf.Min(_tooltip.CalcSize(content).x + 16f, maxW + 16f);
            float x = Mathf.Clamp(mp.x + 16, 2f, Screen.width - w - 6);
            float y = Mathf.Clamp(mp.y + 16, 2f, Screen.height - h - 10);
            GUI.Box(new Rect(x, y, w, h + 8), content, _tooltip);
        }

	}
}
