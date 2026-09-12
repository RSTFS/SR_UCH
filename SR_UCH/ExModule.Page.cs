using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using SR_UCH.Tweaks; //SR / SR.Ctl

namespace SR_UCH_EX {
// EX 操作台页面：本模块自己的 UI（SR_UCH 里不再有任何 EX 页面/文案/行扩展点）。
// 由 ExLoader.Init 调 RegisterUi() 向 SR 注册页面与行扩展点；页面始终中文（SR.Ctl.ForceZh）。
public partial class ExModule {

        //注册 EX 栏目：侧栏位置 + 本模块条目的文案 + 页面渲染 + 行扩展点（由 ExLoader.Init 调一次）
        internal static void RegisterUi() {
            SR.Nav("EX", 100); //侧栏栏目顺序 100（未装 EX 时由侧栏按 ExRef.Loaded 隐藏）
            SR.LocKey("EX", "Invincible", "无敌", null);
            SR.LocDesc("EX", "Invincible", "无敌：免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）", "Invincible: immune to all non-forced deaths (traps/bullets/pits/punches etc.)");
            SR.LocKey("EX", "Fly", "飞天", null);
            SR.LocDesc("EX", "Fly", "飞天：方向键自由飞行（上/下升降，左/右平移，按住 Shift 加速，不按键则悬浮空中）", "Fly: arrow keys move freely in the air, hold Shift to sprint, no key = hover");
            SR.LocKey("EX", "Ignore Host Limit", "无视房主限制", null);
            SR.LocDesc("EX", "Ignore Host Limit", "无视房主房客限制：开启后房客也能执行房主限制的操作（如树屋问号添加/删除等）", "Ignore host/guest limits: guests can use host-only operations (e.g. treehouse question marks)");
            SR.LocKey("EX", "Score Type", "加分类型", null);
            SR.LocDesc("EX", "Score Type", "加分的分数类型（获胜/陷阱击杀/第一/金币等）", "Score type to award (win/trap/first/coin etc.)");
            SR.LocKey("EX", "Allow Clients", "允许客户端删除", null);
            SR.LocDesc("EX", "Allow Clients", "是否允许非房主玩家也删除方块（由房主同步）", "Allow non-host players to destroy blocks too (synced through the host)");
            SR.LocKey("EX", "Ignore Mode Limit", "无视模式限制", null);
            SR.LocDesc("EX", "Ignore Mode Limit", "视野/地图/重生/附加功能等在任何游戏模式下都可用", "Unlock every mode-limited feature in any game mode");
            SR.LocKey("EX", "Freeze Character", "冻结角色", null);
            SR.LocDesc("EX", "Freeze Character", "打开面板/地图时冻结自己的角色，其他角色照常移动", "Freeze your own character while the panel/map is open; others keep moving");
            SR.RegisterPage("EX", RenderConsole);
            SR.RowSliders.Add(TimeScaleSlider);      //本页数值条目：时间流速用滑块
            SR.RowComboWidths.Add(ExComboWidth);    //本页下拉框/编辑框用窄宽
            SR.RowEnumFilters.Add(LevelEnumFilter); //指定关卡下拉过滤不可选关卡
        }

        private static bool TimeScaleSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 2f; fmt = "0.0";
            return e.Definition.Section == "EX" && e.Definition.Key == "Time Scale";
        }

        private static float ExComboWidth(ConfigEntryBase e) { return e.Definition.Section == "EX" ? 80f : 0f; }

        private static bool LevelEnumFilter(ConfigEntryBase e, string enumName, int value) {
            if (e.Definition.Section != "EX" || e.Definition.Key != "Target Level") return true;
            return enumName != "BLANKLEVEL" && value < (int)GameState.LevelName.RANDOM;
        }

        internal static void RenderConsole() {
            bool oldForce = SR.Ctl.ForceZh;
            SR.Ctl.ForceZh = true; //强制中文（EX 页豁免语言切换）
            try {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(
                Enabled ? SR.T("EX总开关：开", "EX master: ON") : SR.T("EX总开关：关", "EX master: OFF"),
                Enabled ? SR.Ctl.SelItem : SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(150)), GUILayout.Height(SR.Ctl.Sc(30)))) {
                Enabled = !Enabled;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(6));

            GUILayout.BeginVertical(SR.Ctl.Footer);
            GUILayout.Label(SR.T("目标: ", "Target: ") + TargetName()
                + SR.T("   （点击表格中的复选框选择目标）", "   (click a checkbox to pick a target)"), SR.Ctl.Label, GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.BeginHorizontal();
            GUILayout.Label(SR.T("号数", "#"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(56)), GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.Label(SR.T("角色", "Animal"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(110)), GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.Label(SR.T("评分", "Score"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(70)), GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.EndHorizontal();
            int curNum = CurrentTargetNumber();
            int rowIdx = 0;
            EnsureDefaultTarget(); //未选中目标时默认选中自己（随后一行 CurrentTargetNumber 刷新）
            curNum = CurrentTargetNumber();
            foreach (PlayerRow row in PlayerTable()) {
                int idx = rowIdx;
                int rnum = row.number;
                bool isSel = rnum == curNum;
                bool isSelf = IsSelf(rnum);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(isSel ? "✓" : "", isSel ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(28)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                    SelectTargetByIndex(idx);
                }
                GUILayout.Label(rnum.ToString(), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(46)), GUILayout.Height(SR.Ctl.Sc(26)));
                GUILayout.Label(row.animal + (isSelf ? SR.T("（我）", " (me)") : ""), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(140)), GUILayout.Height(SR.Ctl.Sc(26)));
                GUILayout.Label(row.score.ToString(), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(70)), GUILayout.Height(SR.Ctl.Sc(26)));
                GUILayout.EndHorizontal();
                rowIdx++;
            }
            GUILayout.Label(SR.T("加分/加金币/踢人 作用于目标", "score/coin/kick affect the target"), SR.Ctl.LabelWrap);
            string posLine = Positions();
            if (posLine.Length > 0) {
                GUILayout.Label(SR.T("坐标: ", "Pos: ") + posLine, SR.Ctl.LabelWrap);
            }
            string loadState = LoadingState();
            if (loadState.Length > 0) {
                GUILayout.Label(loadState, SR.Ctl.LabelWrap);
            }
            GUILayout.EndVertical();
            GUILayout.Space(SR.Ctl.Sc(8));

            bool oldEn = GUI.enabled;
            GUI.enabled = Enabled;
            float exBtnW = SR.Ctl.Sc(150); //所有 EX 操作按钮等宽
            GUILayout.Label(SR.T("— 操作 —", "— Actions —"), SR.Ctl.SecHeader);
            //伪造游戏原生消息，由房主转发。
            GUILayout.BeginHorizontal();
            if (SR.Ctl.HotkeyActionButton(SR.T("踢出目标", "Kick"), SR.T("把目标踢出房间", "Kick the target"), KickKeyEntry, exBtnW, true)) KickTarget();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.BeginHorizontal();
            if (SR.Ctl.HotkeyActionButton(SR.T("清空派对盒道具", "Clear party-box items"), SR.T("清空当前派对盒里可选的道具（派对模式；房主端执行全员可见）", "Clear the items in the current party box (Party mode; host-side clears are visible to everyone)"), ClearPartyBoxKeyEntry, exBtnW, true)) ClearPartyBox();
            if (SR.Ctl.HotkeyActionButton(SR.T("清除地图对象", "Clear map objects"), SR.T("清除地图上玩家放置的全部道具（关卡自带布局不受影响）", "Remove every player-placed prop on the map (the level's own layout is untouched)"), ClearMapObjectsKeyEntry, exBtnW, true)) ClearMapObjects();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            //走游戏原生 PointAwarded（全员可见）；分值按类型标准值（获胜 50、陷阱 10 等）。
            GUILayout.BeginHorizontal();
            bool enCtl = GUI.enabled;
            if (ScoreTypeEntry != null) {
                SR.Ctl.RenderControl(ScoreTypeEntry);
            }
            GUI.enabled = enCtl;
            if (SR.Ctl.HotkeyActionButton(SR.T("加分", "Score"), SR.T("给目标按类型标准分值加分", "Award points by type value"), ScoreKeyEntry, exBtnW, true)) AddScore();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.BeginHorizontal();
            if (CoinAmountEntry != null) {
                SR.Ctl.RenderControl(CoinAmountEntry);
            }
            GUI.enabled = enCtl;
            if (SR.Ctl.HotkeyActionButton(SR.T("加金币", "Coin"), SR.T("给目标加金币", "Add coins"), CoinKeyEntry, exBtnW, true)) AddCoin();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.Label(SR.T("— 受限功能 —", "— Restricted —"), SR.Ctl.SecHeader);
            GUILayout.BeginHorizontal();
            if (SR.Ctl.HotkeyActionButton(SR.T("复活", "Respawn"), SR.T("让自己重生回起点", "Respawn yourself"), RespawnKeyEntry, exBtnW, true)) RespawnTarget();
            if (SR.Ctl.HotkeyActionButton(SR.T("杀死目标", "Kill target"), SR.T("让目标立即死亡（需房主/单机权限）", "Kill the target instantly (host/single-player only)"), KillKeyEntry, exBtnW, true)) KillTarget();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.BeginHorizontal();
            if (SR.Ctl.HotkeyActionButton(SR.T("获胜", "Win"), SR.T("让自己到达终点获胜", "Win as yourself"), WinKeyEntry, exBtnW, true)) WinTarget();
            if (SR.Ctl.HotkeyActionButton(SR.T("结束对局", "End match"), SR.T("当前对局立即进入结算", "End the match into scoring"), EndRoundKeyEntry, exBtnW, true)) EndRound();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUILayout.BeginHorizontal();
            if (LivesAmountEntry != null) {
                SR.Ctl.RenderControl(LivesAmountEntry);
            }
            GUI.enabled = enCtl;
            if (SR.Ctl.HotkeyActionButton(SR.T("加生命", "Lives"), SR.T("改自己剩余生命", "Change your lives"), LivesKeyEntry, exBtnW, true)) AddLives();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            //需服务器权限：仅单机/本地派对/房主有效。
            GUILayout.BeginHorizontal();
            if (TargetLevelEntry != null) {
                SR.Ctl.RenderControl(TargetLevelEntry);
            }
            GUI.enabled = enCtl;
            if (SR.Ctl.HotkeyActionButton(SR.T("指定关卡", "Force level"), SR.T("树屋大厅直接开始所选关卡", "Start the chosen level"), ForceLevelKeyEntry, exBtnW, true)) ForceLevel();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            //自身状态（仅本地生效）。
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.Label(SR.T("— 自身状态 —", "— Self states —"), SR.Ctl.SecHeader);
            GUILayout.BeginHorizontal();
            if (SR.Ctl.SelfToggleButton(SR.T("无敌：", "Invincible: "), SR.T("自己免疫非强制死亡", "Immune to non-forced deaths"), InvincibleOn, InvincibleKeyEntry, SR.Ctl.Sc(150), true)) ToggleInvincible();
            if (SR.Ctl.SelfToggleButton(SR.T("飞天：", "Fly: "), SR.T("方向键自由飞行", "Fly with arrow keys"), FlyOn, FlyKeyEntry, SR.Ctl.Sc(150), true)) ToggleFly();
            if (SR.Ctl.SelfToggleButton(SR.T("蹲移：", "Duck: "), SR.T("保持蹲下自由移动", "Stay ducked and move freely"), CrouchMoveOn, CrouchMoveKeyEntry, SR.Ctl.Sc(150), true)) ToggleCrouchMove();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            GUI.enabled = oldEn;
            GUILayout.Space(SR.Ctl.Sc(6));
            //开关项用复选框，右键可绑键（整行都是命中区）。
            GUILayout.BeginHorizontal();
            ConfigEntryBase ac = _allowClients; //本模块自己的条目（EX/Allow Clients）
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("允许客户端删除", "Allow clients delete") + SR.Ctl.KeySuffix(_allowClientsKey), SR.T("非房主玩家也能删除方块（由房主同步；需先开启方块破坏）", "Non-host players can destroy blocks too (synced through the host; requires Destroy Blocks on)")), ac, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
            SR.Ctl.RegisterRowHotkey(_allowClientsKey);
            if (GUILayout.Button(AllowClientsOn ? "✓" : "", AllowClientsOn ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(30)), GUILayout.Height(SR.Ctl.Sc(26)))) ToggleAllowClients();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(2));
            //无视模式限制：独立于总开关，解锁所有仅自由模式的功能（视野/地图/重生等）。
            GUILayout.BeginHorizontal();
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("无视模式限制", "Ignore mode limit") + SR.Ctl.KeySuffix(_ignoreModeLimitKey), SR.T("视野/地图/重生/附加功能等在任何游戏模式下都可用", "Unlock every mode-limited feature in any game mode")), _ignoreModeLimitEntry, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
            SR.Ctl.RegisterRowHotkey(_ignoreModeLimitKey);
            if (GUILayout.Button(IgnoreModeLimit ? "✓" : "", IgnoreModeLimit ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(30)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                IgnoreModeLimit = !IgnoreModeLimit;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase ih = _ignoreHostLimitEntry; //本模块自己的条目（EX/Ignore Host Limit）
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("无视房主限制", "Ignore host limit") + SR.Ctl.KeySuffix(IgnoreHostLimitKeyEntry), SR.T("开启后房客也能执行房主限制的操作（如树屋问号添加/删除等）", "When on, guests can use host-only operations (e.g. treehouse question marks)")), ih, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
            SR.Ctl.RegisterRowHotkey(IgnoreHostLimitKeyEntry);
            if (GUILayout.Button(IgnoreHostLimit ? "✓" : "", IgnoreHostLimit ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(30)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                IgnoreHostLimit = !IgnoreHostLimit;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(2));
            //打开面板/地图时只冻结自己（默认关 = 游戏照常运行）。
            GUILayout.BeginHorizontal();
            SR.Ctl.RestoreLabel(new GUIContent(SR.T("冻结角色", "Freeze self") + SR.Ctl.KeySuffix(_freezeCharKey), SR.T("打开面板/地图时冻结自己的角色，其他角色照常移动（默认关闭：打开面板/地图时游戏照常运行、自己也能动）", "Freeze your own character while the panel/map is open; other characters keep moving (OFF by default: game keeps running and you can move)")), _freezeCharEntry, SR.Ctl.Sc(140), SR.Ctl.Sc(26));
            SR.Ctl.RegisterRowHotkey(_freezeCharKey);
            if (GUILayout.Button(PauseGame ? "✓" : "", PauseGame ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(30)), GUILayout.Height(SR.Ctl.Sc(26)))) {
                PauseGame = !PauseGame;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(4));
            } finally {
                SR.Ctl.ForceZh = oldForce;
            }
        }

}
}
