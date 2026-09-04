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

// ==== 分区：Pages（各栏目页面渲染：首页/地图/EX/实验/快速调整/会话内容）====

        //快速调整 page: 分数折扣 + 快速切换 + 快速自杀 (三个分区)
        private static void RenderQuickAdjustConsole() {
            GUILayout.Label(T("— 分数折扣 —", "— Score discount —"), _secHeader);
            //折扣数值：默认滑块（0/10/20...90 整十倍数，与其它滑块条一致的 DrawSlider 样式）；
            //「更多折扣数值」开 → 编辑框自由 0-90。更多折扣选择框放在"恢复原值"右侧。
            GUILayout.BeginHorizontal();
            ConfigEntryBase disc = FindInternalEntry("快速调整", "Score Discount");
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
            if (GUILayout.Button(new GUIContent(T("应用折扣", "Apply"), T("把自己 handicap 设为 100-折扣 %。\n滑块模式（0-90 整十）：0 = 关闭；90 → handicap 10（上限 90%）。\n本局立即生效，平衡板上可见；恢复原值可还原为 100%。", "Set your handicap to 100-discount %.\nSlider mode (0-90 in tens): 0 = off; 90 → handicap 10 (90% cap).\nTakes effect immediately and shows on the balancer; Restore sets it back to 100%.")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.ApplyScoreDiscount();
            }
            GUILayout.Space(Sc(8));
            if (GUILayout.Button(new GUIContent(T("恢复原值", "Restore"), T("把自己的 handicap 恢复为 100%（清除折扣）", "Restore your handicap to 100% (clears the discount)")), _btn, GUILayout.Width(Sc(140)), GUILayout.Height(Sc(30)))) {
                Experiments.RestoreScoreDiscount();
            }
            GUILayout.Space(Sc(8));
            //更多折扣数值选择框（放到恢复原值右侧；悬浮说明直接挂在选择框 tooltip 上）
            ConfigEntryBase more = FindInternalEntry("快速调整", "More Discount Values");
            if (more != null) {
                string moreTip = T("更多折扣数值（默认关闭）：开启后折扣滑块变为自由输入框（0-90 任意整数，0 = 关闭）。\n",
                    "More discount values (OFF by default): turns the slider into a free input box (0-90 any integer, 0 = off).\n");
                bool nm = GUILayout.Toggle(moreOn, new GUIContent(T("更多折扣数值", "More values"), moreTip), GUILayout.Width(Sc(160)), GUILayout.Height(Sc(30)));
                if (nm != moreOn) SetValue(more, nm);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            //快速重试（仅挑战模式）：死后自动重试 + 快速重试 + 等待秒数滑块
            GUILayout.Label(T("— 快速重试 —", "— Quick retry —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase qr = FindInternalEntry("快速调整", "Quick Retry");
            RestoreLabel(new GUIContent(T("死后自动重试", "Auto retry on death"), T("仅挑战模式：死亡后自动触发重试（重开关卡），无需长按。\n原理：修改重生延迟在挑战模式的最短时间自动重试。\n默认关闭；对局中开关立即生效。", "Challenge only: auto-retry (restart the run) after death, no hold needed.\nUses the respawn-delay challenge logic.\nOFF by default; takes effect immediately.")), qr, Sc(140), Sc(52));
            if (qr != null) RenderControl(qr);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase qrf = FindInternalEntry("快速调整", "Quick Retry Fast");
            RestoreLabel(new GUIContent(T("快速重试", "Quick retry"), T("仅挑战模式：死亡后长按 B 到设定等待秒数自动重试。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Challenge only: after death, hold B for the set seconds to auto-retry.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qrf, Sc(140), Sc(52));
            if (qrf != null) RenderControl(qrf);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //等待秒数滑块（0-2，步长 0.1；与折扣滑块同宽）
            GUILayout.BeginHorizontal();
            ConfigEntryBase qrh = FindInternalEntry("快速调整", "Quick Retry Hold");
            GUILayout.Label(T("等待秒数", "Hold s"), _label, GUILayout.Width(Sc(84)), GUILayout.Height(Sc(26)));
            if (qrh != null) {
                float hv = (float)qrh.BoxedValue;
                Rect hr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                float hn = DrawSlider(hr, hv, 0f, 2f, false);
                hn = Mathf.Round(hn * 10f) / 10f; //吸附 0.1
                if (Mathf.Abs(hn - hv) > 0.0001f) SetValue(qrh, hn);
                GUILayout.Label(T(hn.ToString("0.0") + "s", hn.ToString("0.0") + "s"), _label, GUILayout.Width(Sc(44)), GUILayout.Height(Sc(26)));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(6));

            //快速切换（仅自由模式）：快速切换 + 等待秒数滑块
            GUILayout.Label(T("— 快速切换 —", "— Quick switch —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase qs = FindInternalEntry("快速调整", "Quick Switch");
            RestoreLabel(new GUIContent(T("快速切换", "Quick switch"), T("仅自由模式：长按 B 到设定等待秒数切换 行动↔建造 模式。\n选中后由下方滑块条修改长按 B 的等待时间（游戏默认 0.5 秒）。\n默认关闭；对局中开关立即生效。", "Freeplay only: hold B for the set seconds to switch Action↔Build.\nWhen on, the slider below sets the hold-B wait time (vanilla 0.5s).\nOFF by default; takes effect immediately.")), qs, Sc(140), Sc(52));
            if (qs != null) RenderControl(qs);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //等待秒数滑块（0-2，步长 0.1；与折扣滑块同宽）
            GUILayout.BeginHorizontal();
            ConfigEntryBase qsh = FindInternalEntry("快速调整", "Quick Switch Hold");
            GUILayout.Label(T("等待秒数", "Hold s"), _label, GUILayout.Width(Sc(84)), GUILayout.Height(Sc(26)));
            if (qsh != null) {
                float shv = (float)qsh.BoxedValue;
                Rect shr = GUILayoutUtility.GetRect(Sc(150), Sc(28));
                float shn = DrawSlider(shr, shv, 0f, 2f, false);
                shn = Mathf.Round(shn * 10f) / 10f; //吸附 0.1
                if (Mathf.Abs(shn - shv) > 0.0001f) SetValue(qsh, shn);
                GUILayout.Label(T(shn.ToString("0.0") + "s", shn.ToString("0.0") + "s"), _label, GUILayout.Width(Sc(44)), GUILayout.Height(Sc(26)));
            }
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
            GUILayout.Space(Sc(4));
            //树屋地图：允许树屋用地图（地图主要自由模式）
            GUILayout.BeginHorizontal();
            ConfigEntryBase tm = FindInternalEntry("地图", "Treehouse Map");
            RestoreLabel(new GUIContent(T("树屋地图", "Treehouse map"), T("允许在树屋大厅使用地图（M 键开）。地图主要用于自由模式；关闭后树屋不能开地图。", "Allow using the map in the treehouse (M key). The map is mainly for Freeplay; when off the map can't open in the treehouse.")), tm, Sc(140), Sc(26));
            if (GUILayout.Button(TreehouseMap ? "✓" : "", TreehouseMap ? _checkOn : _checkOff, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) {
                if (tm != null) tm.BoxedValue = !TreehouseMap;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
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
            ConfigEntryBase ga = FindInternalEntry("地图", "Grid Always On");
            RestoreLabel(new GUIContent(T("地图网格", "Map grid"), T("建造阶段任何模式都显示网格；本开关让自由模式对局在行动阶段也保持网格（游戏默认行动阶段不显示）。", "The build grid shows while building in every mode; this switch keeps it on during the play phase in freeplay matches only (vanilla hides it during play).")), ga, Sc(140), Sc(52));
            if (ga != null) RenderControl(ga);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //视野（原“视野”栏目并入“自由模式”）
            GUILayout.Label(T("— 视野 —", "— Camera —"), _secHeader);
            RenderSectionGroup("视野");
            GUILayout.Space(Sc(4));
            //重生（原“重生”栏目并入“自由模式”）
            GUILayout.Label(T("— 重生 —", "— Respawn —"), _secHeader);
            RenderSectionGroup("Respawn");
            GUILayout.Space(Sc(4));
        }

        //按 config section 渲染其全部条目（通用行）——把原“视野/重生”栏目并入本页
        static void RenderSectionGroup(string sec) {
            if (_internalConfig == null) return;
            //列宽给足，避免并入的条目名在窄列折行（如“重生功能总开关”）
            float col = Mathf.Max(Sc(180), _winWidth - SidebarWidth() - Sc(240));
            foreach (ConfigEntryBase e in AllEntries(_internalConfig)) {
                if (e.Definition.Section != sec) continue;
                RenderEntryRow(e, true, col);
            }
        }

        //关卡(Level)页：重载关卡 / 广播方块快照（仅派对/创意局内、房主）
        private static void RenderLevelPage() {
            //重载关卡：执行按钮在上，重载模式选择在下
            GUILayout.Label(T("— 重载关卡 —", "— Reload level —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("重载关卡", "Reload level"),
                T("房主把当前视角的所有方块写入关卡快照 → 真正重载当前关卡场景（全员重新加载）。\n重载后按快照重建方块并广播：所有方块（含玩家放置的、可移动的）不丢；\n按下方“重载模式”决定是否补分保留分数。\n⚠ 仅派对/创意局内生效；仅房主有效。", "Host writes the current blocks into the snapshot → truly reloads the current level scene (everyone reloads).\nRebuilds blocks from the snapshot: every block is kept;\nscore fill depends on the Reload mode below.\n⚠ Party/Creative only; host only.")),
                _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                Experiments.ReloadLevel();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            GUILayout.BeginHorizontal();
            ConfigEntryBase rlm = FindInternalEntry("关卡", "Reload Mode");
            RestoreLabel(new GUIContent(T("重载模式", "Reload mode"),
                T("重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按原类型分块给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks kept; the host fills scores so everyone sees the same score/types next tally.\nKeep blocks only (skip fill) = blocks kept, score resets.")),
                rlm, Sc(140), Sc(52));
            if (rlm != null) RenderControl(rlm);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //广播方块快照
            GUILayout.Label(T("— 广播方块快照 —", "— Broadcast snapshot —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("广播方块快照", "Broadcast snapshot"),
                T("房主把当前视角的所有方块（含位置/旋转/属性）打包广播 → 全员按房主视角重建方块。\n用于修复偶发的“局内方块在自己视野消失/不同步”。\n不重载场景：对局进度与分数保留，全员短暂卡顿后方块即重建。\n⚠ 仅派对/创意局内生效；仅房主有效。", "Host packages all blocks in their view and broadcasts → everyone rebuilds blocks from the host snapshot.\nFixes occasional in-match blocks disappearing/desyncing.\nNo scene reload: progress and scores are kept.\n⚠ Party/Creative only; host only.")),
                _btn, GUILayout.Width(Sc(170)), GUILayout.Height(Sc(30)))) {
                Experiments.BroadcastSnapshot();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));

            //派对盒子炸弹（关卡栏分区）：房主开启后，对局内所有成员都发「炸弹！」快捷消息才生成
            GUILayout.Label(T("— 派对盒子炸弹 —", "— Party-box bomb —"), _secHeader);
            GUILayout.BeginHorizontal();
            ConfigEntryBase pbe = FindInternalEntry("EX", "Party Bomb");
            RestoreLabel(new GUIContent(T("派对盒子炸弹", "Party-box bomb"), T("房主开启后，对局内所有成员都发过「炸弹！」快捷短语时，往当前派对盒子塞入炸弹。", "Host on: when every member sends the Bomb quick-phrase, spawn a bomb in the current party box.")), pbe, Sc(140), Sc(52));
            if (GUILayout.Button(PartyBomb.Enabled ? "✓" : "", PartyBomb.Enabled ? _checkOn : _checkOff, GUILayout.Width(Sc(30)), GUILayout.Height(Sc(26)))) {
                if (pbe != null) pbe.BoxedValue = !PartyBomb.Enabled;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(2));
            //炸弹种类下拉（固定三档：小/大/超级）
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("炸弹种类", "Bomb type"), _label, GUILayout.Width(Sc(84)), GUILayout.Height(Sc(26)));
            ConfigEntryBase pbtype = FindInternalEntry("EX", "Party Bomb Type");
            if (pbtype != null) {
                //三档：0小 / 1大 / 2超级（对应游戏内 99abombmini / 99bomb / 99bbombmega）
                string[] opts = new[] { T("小炸弹", "Small"), T("大炸弹", "Big"), T("超级炸弹", "Mega") };
                int curIdx = Mathf.Clamp((int)pbtype.BoxedValue, 0, 2);
                bool open; if (!_editOpen.TryGetValue(pbtype, out open)) open = false;
                int sel = ComboBox(pbtype, opts[curIdx], null, opts, ref open, Sc(150));
                if (sel >= 0) { pbtype.BoxedValue = sel; _editOpen[pbtype] = false; }
                else _editOpen[pbtype] = open;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
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
            ExRef.EnsureDefaultTarget(); //未选中目标时默认选中自己（随后一行 CurrentTargetNumber 刷新）
            curNum = ExRef.CurrentTargetNumber();
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
            GUILayout.Label(T("— 操作 —", "— Actions —"), _secHeader);
            //踢出目标（伪造游戏原生消息，房主原生转发）
            GUILayout.BeginHorizontal();
            if (HotkeyActionButton(T("踢出目标", "Kick"), T("把目标踢出房间", "Kick the target"), ExRef.KickKeyEntry)) ExRef.KickTarget();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //清空派对盒道具
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(T("清空派对盒道具", "Clear party-box items"), T("清空当前派对盒里可选的道具（派对模式）", "Clear the items in the current party box (Party mode)")), _btn, GUILayout.Width(bw), GUILayout.Height(bh))) ExRef.ClearPartyBox();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //score: type dropdown + apply（游戏原生 PointAwarded 全员；分值按类型标准值：获胜 50、陷阱 10 等）
            GUILayout.BeginHorizontal();
            bool enCtl = GUI.enabled;
            if (ExRef.ScoreTypeEntry != null) {
                RenderControl(ExRef.ScoreTypeEntry);
            }
            GUI.enabled = enCtl;
            if (HotkeyActionButton(T("加分", "Score"), T("给目标按类型标准分值加分", "Award points by type value"), ExRef.ScoreKeyEntry)) ExRef.AddScore();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //coin amount + button（游戏原生 PointAwarded(coin) 全员）
            GUILayout.BeginHorizontal();
            if (ExRef.CoinAmountEntry != null) {
                RenderControl(ExRef.CoinAmountEntry);
            }
            GUI.enabled = enCtl;
            if (HotkeyActionButton(T("加金币", "Coin"), T("给目标加金币", "Add coins"), ExRef.CoinKeyEntry)) ExRef.AddCoin();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //受限功能：获胜 / 复活 / 加生命 / 指定关卡
            GUILayout.Space(Sc(2));
            GUILayout.Label(T("— 受限功能 —", "— Restricted —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (HotkeyActionButton(T("复活", "Respawn"), T("让自己重生回起点", "Respawn yourself"), ExRef.RespawnKeyEntry)) ExRef.RespawnTarget();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            if (HotkeyActionButton(T("获胜", "Win"), T("让自己到达终点获胜", "Win as yourself"), ExRef.WinKeyEntry)) ExRef.WinTarget();
            if (HotkeyActionButton(T("结束对局", "End match"), T("当前对局立即进入结算", "End the match into scoring"), ExRef.EndRoundKeyEntry)) ExRef.EndRound();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            if (ExRef.LivesAmountEntry != null) {
                RenderControl(ExRef.LivesAmountEntry);
            }
            GUI.enabled = enCtl;
            if (HotkeyActionButton(T("加生命", "Lives"), T("改自己剩余生命", "Change your lives"), ExRef.LivesKeyEntry)) ExRef.AddLives();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //指定关卡放加生命下面（需服务器权限：仅单机/本地派对/房主有效）
            GUILayout.BeginHorizontal();
            if (ExRef.TargetLevelEntry != null) {
                RenderControl(ExRef.TargetLevelEntry);
            }
            GUI.enabled = enCtl;
            if (HotkeyActionButton(T("指定关卡", "Force level"), T("树屋大厅直接开始所选关卡", "Start the chosen level"), ExRef.ForceLevelKeyEntry)) ExRef.ForceLevel();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            //自身状态（本地生效）
            GUILayout.Space(Sc(2));
            GUILayout.Label(T("— 自身状态 —", "— Self states —"), _secHeader);
            GUILayout.BeginHorizontal();
            if (SelfToggleButton(T("无敌：", "Invincible: "), T("自己免疫非强制死亡", "Immune to non-forced deaths"), ExRef.InvincibleOn, ExRef.InvincibleKeyEntry)) ExRef.ToggleInvincible();
            if (SelfToggleButton(T("飞天：", "Fly: "), T("方向键自由飞行", "Fly with arrow keys"), ExRef.FlyOn, ExRef.FlyKeyEntry)) ExRef.ToggleFly();
            GUILayout.EndHorizontal();
            GUILayout.Space(Sc(4));
            GUILayout.BeginHorizontal();
            if (SelfToggleButton(T("蹲移：", "Duck: "), T("保持蹲下自由移动", "Stay ducked and move freely"), ExRef.CrouchMoveOn, ExRef.CrouchMoveKeyEntry)) ExRef.ToggleCrouchMove();
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

        //自身状态开关按钮：左键切换 on/off（快捷键按下即切换功能）。
        //keyEntry 为对应功能的快捷键配置（EX 模块暴露），按钮上显示当前绑定键。
        private static bool SelfToggleButton(string label, string tooltip, bool on, ConfigEntry<KeyCode> keyEntry) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            string keyTxt = "";
            if (keyEntry != null && keyEntry.Value != KeyCode.None) keyTxt = " [" + KeyDisplayName(keyEntry.Value) + "]";
            string text = label + (on ? T("开", "ON") : T("关", "OFF")) + keyTxt
                + (capturing ? " " + T("按键..", "key..") : "");
            //GetRect 指定固定宽度（限宽），GUI.Button 只负责绘制；点击由下面手动事件检测，
            //这样右键明确进入设置/删除，不会误触按钮操作，也不会被 GUILayout.Button 抢走右键。
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text, tooltip), _btn,
                GUILayout.Width(Sc(150)), GUILayout.Height(Sc(30)));
            bool clicked = false;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition)) {
                if (e.button == 0 && !capturing) { clicked = true; e.Use(); }
                else if (e.button == 1 && keyEntry != null) { HandleHotkeyRightClick(keyEntry, capturing); e.Use(); }
            }
            GUI.Button(r, new GUIContent(text, tooltip), capturing ? _capture : _btn);
            return clicked; //左键（非捕捉）触发切换
        }

        //操作按钮：左键触发操作（快捷键按下即触发操作）。
        //keyEntry 为对应操作的快捷键配置（EX 模块暴露），按钮上显示当前绑定键。
        private static bool HotkeyActionButton(string label, string tooltip, ConfigEntry<KeyCode> keyEntry) {
            bool capturing = keyEntry != null && _capturing == keyEntry;
            string keyTxt = "";
            if (keyEntry != null && keyEntry.Value != KeyCode.None) keyTxt = " [" + KeyDisplayName(keyEntry.Value) + "]";
            string text = label + keyTxt + (capturing ? " " + T("按键..", "key..") : "");
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text, tooltip), _btn,
                GUILayout.Width(Sc(150)), GUILayout.Height(Sc(30)));
            bool clicked = false;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition)) {
                if (e.button == 0 && !capturing) { clicked = true; e.Use(); }
                else if (e.button == 1 && keyEntry != null) { HandleHotkeyRightClick(keyEntry, capturing); e.Use(); }
            }
            GUI.Button(r, new GUIContent(text, tooltip), capturing ? _capture : _btn);
            return clicked; //左键（非捕捉）触发操作
        }

        //右键处理：未捕捉 → 进入捕捉（等待按键设为快捷键）；捕捉中再右键 → 删除快捷键。
        //（删除也可在捕捉中直接按 Esc，由 SR 的按键捕捉机制处理。）
        private static void HandleHotkeyRightClick(ConfigEntry<KeyCode> keyEntry, bool capturing) {
            if (keyEntry == null) return;
            if (capturing) {
                try { keyEntry.BoxedValue = KeyCode.None; } catch { }
                _capturing = null;
                _dirty = true;
            } else {
                _prevBoxed = keyEntry.BoxedValue;
                _capturing = keyEntry;
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
            //A 组（游戏时长>17h16m18s 或 奔跑长度>52000m）：建造增强（无视碰撞）、树屋问号
            //B 组（游戏时长>52h 或 奔跑长度>100000m）：方块破坏
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
        }

        //会话内容页：顶部工具行（固定在滚动区外，不随内容滚动）
        private static void RenderChatLogToolbar() {
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
            GUILayout.Space(Sc(4));
        }

        //the 会话内容 page: 大型编辑框显示全部记录（工具行固定在滚动区外，见 Window.cs）
        private static void RenderChatLog() {
            //大型滚动区（自动换行、可滚动）显示全部记录
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
                float w = Mathf.Max(Sc(140), _winWidth - SidebarWidth() - Sc(36));
                float innerW = w - Sc(20);
                //聊天文本直接放外层滚动区（避免文本框与外层内容区双滚动条）；完整高度可滚看全部
                float th = Mathf.Max(Sc(200), _chatLabel.CalcHeight(new GUIContent(_chatTextCache), innerW) + Sc(8));
                GUILayout.Label(_chatTextCache, _chatLabel, GUILayout.Width(innerW), GUILayout.Height(th));
                if (entries.Count > _lastChatCount) { _scroll.y = float.MaxValue; } //新消息滚到底
                _lastChatCount = entries.Count;
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
