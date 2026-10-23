// 关卡栏目：栏目注册、两页页签、生命周期。
// 第 1 页 派对盒 -> Level.PartyBox.cs；第 2 页 关卡工具 -> Level.Tool.cs。
// 本命名空间自定义的 Level 会遮蔽游戏的 global::Level：引用游戏类型一律写 global::Level。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
namespace SR_UCH.Tweaks {
// 两页共用一个 partial 类：页面渲染分别在 Level.PartyBox.cs / Level.Tool.cs。
[SrFeature("sr.level", "Level", "1.0")]
public partial class Level : ITweak {

    // 栏目注册：本地化文案、侧栏顺序、页签、快捷键。
    private static void SelfReg() {
// 反射游戏私有成员登记：改名时启动自检会在日志告警。
        SRApi.Ref.Override("ScoreKeeper.historyPointBlocks", "重载保分（历史分块）");
        // 快照加载后据此区分关卡自带方块与玩家放置方块。
        SRApi.Ref.Override("QuickSaver.initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
        SRApi.LocSec("Level", "关卡", null);
        SRApi.Nav("Level", 40); //侧栏栏目顺序 40
        // 一个栏目两页，页签切换。
        SRApi.RegisterPage("Level", "partybox", "派对盒", "Party Box", 10, RenderPartyBox);
        SRApi.RegisterPage("Level", "tools", "关卡工具", "Level Tools", 20, RenderTools);
        SRApi.LocKey("Level", "Reload Mode", "重载模式", null);
        SRApi.LocDesc("Level", "Reload Mode", "关卡重载模式\n保留方块和分数＝保留方块并给全员补分；只保留方块＝保留方块但分数清零\n补分在下一回合结算时显示，未安装本模组的房客同样生效。", "level reload mode\nKeep blocks and score = blocks kept and scores refilled; Blocks only = blocks kept but scores reset\nrefill appears at the next round settlement and also covers clients without this mod");
        // 预登记本页自动快捷键：不预登记则重启后在首次打开本页前按键无效；渲染时 HotkeyButton 会再登记一次（同 id 幂等）。
        SRApi.HotkeyAction("level.reload", "重载关卡", () => Level.ReloadLevel());
        SRApi.HotkeyAction("level.broadcast", "广播方块快照", () => Level.BroadcastSnapshot());
    }

    public void Initialize(IFeatureHost plugin) {
        SelfReg();
        // 对外槽位：其它功能经 Slots.Require 取用清场能力，无需引用 Level 类型。
        Slots.Provide(SlotClearPlacedBlocks, (Action<List<Placeable>, string>)ClearPlacedBlocks);
        // 对外槽位：关卡重载忙碌状态（布局预设等功能据此避免与本功能的重载并发）。
        Slots.Provide("level.reloadBusy", (Func<bool>)(() => _reloadBusy));
        _reloadMode = plugin.Config.Bind<ReloadMode>("Level", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
			// CreateAndPatchAll 不自动发现带 [HarmonyPatch] 的独立类，必须显式注册；单个补丁目标失效只丢本类功能，不影响其它功能。
        try { Harmony.CreateAndPatchAll(typeof(Level), (string)null); }
        catch (Exception e) { SRApi.LogError("Level 补丁注册失败: " + e.Message); }
    }
}
}