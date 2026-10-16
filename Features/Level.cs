// 关卡。侧栏「关卡」栏目的共用部分：栏目注册、两页的页签、以及生命周期。
//   第 1 页 派对盒   -> Level.PartyBox.cs
//   第 2 页 关卡工具 -> Level.Tool.cs
// **命名提醒**：本命名空间里自定义的 Level 会**遮住游戏自带的 global::Level**。引用游戏那个 Level 时
//   一律写 global::Level；核心 SR.Core 的 typeof(Level) 已同步改成 typeof(global::Level)。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
namespace SR_UCH.Tweaks {
// 两页共用一个类：页面渲染分别在 Level.PartyBox.cs / Level.Tool.cs。
public partial class Level : ITweak {

    //本文件界面文案（自包含）
    private static void SelfReg() {
//本文件反射的游戏私有成员（游戏更新改名时：启动自检会在日志里告警；改名需等新版模组适配）
        SR.RefOverride("ScoreKeeper.historyPointBlocks", "重载保分（历史分块）");
        // **补登记**：快照加载后"哪些方块是关卡自带的"靠它算（InitialLevelPieces），
        //  原来裸反射没登记 -> 改名后静默失效（构建归属会把关卡自带方块也算成玩家放置）。
        //  注：同一处的 GameControl."currentPhase" 是不存在的成员（功能因此从未运行），
        //  已改为直接用 public 的 GameControl.Phase，不再是反射，故无需登记。
        SR.RefOverride("QuickSaver.initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
        SR.LocSec("Level", "关卡", null);
        SR.Nav("Level", 40); //侧栏栏目顺序 40
        // **R433**：一个栏目两页（页签切换，见 SR.Ctl.RegisterPage 多页说明）
        SR.RegisterPage("Level", "partybox", "派对盒", "Party Box", 10, RenderPartyBox);
        SR.RegisterPage("Level", "tools", "关卡工具", "Level Tools", 20, RenderTools);
        SR.LocKey("Level", "Reload Mode", "重载模式", null);
        SR.LocDesc("Level", "Reload Mode", "保留方块和分数: 重载后方块留着, 房主按重载前的分数类型给全员补一次, 下一回合结算时分数和图标都和重载前一样, 没装这个的房客也算上. 只保留方块: 方块留着但分数清零, 相当于重新开一局.", "Reload level mode:\nKeep blocks & score (allow fill) = blocks are kept; the host broadcasts a fill using the pre-reload per-type blocks (win/coin/trap etc. as-is), so everyone (incl. clients without the mod) sees the same score and types as before at the next round's tally (fill does not trigger an immediate tally; icons appear with the normal round end).\nKeep blocks only (skip fill) = blocks are kept, score resets (fresh match, no fill).");
        // 预登记本页的自动快捷键（不用等页面首次渲染）：否则重启后这些键在打开关卡页之前
        //  根本没登记过, SR.CheckHotkeys 轮询不到，表现为"绑了键、重启后按了没反应"。
        //  渲染时的 HotkeyButton 仍会再登记一次（同一 id 幂等，只覆盖 label/action）。
        SR.HotkeyAction("level.reload", "重载关卡", () => Level.ReloadLevel());
        SR.HotkeyAction("level.broadcast", "广播方块快照", () => Level.BroadcastSnapshot());
    }

    public void Initialize(IFeatureHost plugin) {
        SelfReg();
        //对外槽位：别的功能/外部模块经 SR.Require 取用清场能力，不引用 Level 类型。
        SR.Provide(SlotClearPlacedBlocks, (Action<List<Placeable>, string>)ClearPlacedBlocks);
        // **Opt-14（审计 D#24）**：这里原来是 `((BaseUnityPlugin)plugin).Config` 硬转。
        //  宿主不是 BaseUnityPlugin 时会抛 InvalidCastException，被外层吞掉 -> 整个关卡栏目静默消失
        //  （QuickAdjust.cs 早就为同一个问题改成了契约成员，这里漏了）。
        //  契约 IFeatureHost.Config 就是同一个 ConfigFile，直接用即可。
        _reloadMode = plugin.Config.Bind<ReloadMode>("Level", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
			//注册本类的 Harmony 补丁：CreateAndPatchAll 不会自动发现带 [HarmonyPatch] 的类型，
			//独立类必须自己注册，否则这些补丁写了也不生效（重载保分 / 快照广播全部失效）。
			// **Opt-15（审计 D#23）**：数量原来写"9 个"，实际本程序集（Level.cs + Level.PartyBox.cs
			//  + Level.Tool.cs）共 **11** 个 [HarmonyPatch]（Level.Tool.cs 10 个 + Level.PartyBox.cs 1 个）。
			//同类写法见 Experiments 的 Reeval / ExModule.Other.cs 的 InitMapGrid / QuickAdjust 的初始化。
			//带 try/catch：单个补丁目标失效（游戏更新改方法名）只丢本类功能，不影响其它功能,
			//参考 BuildingTools.cs（它是全项目唯一原本就分开注册 + 容错的地方）。
        try { Harmony.CreateAndPatchAll(typeof(Level), (string)null); }
        catch (Exception e) { SR.LogError("Level 补丁注册失败: " + e.Message); }
    }
}
}