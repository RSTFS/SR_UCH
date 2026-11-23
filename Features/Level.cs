// 关卡栏目：栏目注册、两页页签、生命周期；页面见 Level.PartyBox.cs / Level.Tool.cs。
// 本命名空间自定义的 Level 会遮蔽游戏 global::Level：引用游戏类型一律写 global::Level。
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
namespace SR_UCH.Tweaks {
[SrFeature("sr.level", "Level", "1.0")]
public partial class Level : ITweak {

    // 重载锁自愈：协程被场景切换掐断时无人复位，超时即视为残留并放锁。
    private static void TickReloadLock() {
        try {
            if (!ReloadLockStuck()) return;
            SetReloadBusy(false);
            SRApi.LogWarn("[重载] 重载锁超时未复位，已自动释放（tick 兜底）");
        } catch (Exception __ex) { SRApi.Guard.Log("关卡重载/锁自愈", __ex); }
    }

    private static void SelfReg() {
// 反射游戏私有成员登记：改名时启动自检会在日志告警。
        SRApi.Ref.Override("ScoreKeeper.historyPointBlocks", "重载保分（历史分块）");
        // 快照加载后据此区分关卡自带方块与玩家放置方块。
        SRApi.Ref.Override("QuickSaver.initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
        SRApi.LocSec("Level", "关卡", null);
        SRApi.Nav("Level", 50); //侧栏栏目顺序 40
        SRApi.RegisterPage("Level", "partybox", "派对盒", "Party Box", 10, RenderPartyBox);
        SRApi.RegisterPage("Level", "tools", "关卡工具", "Level Tools", 20, RenderTools);
        TweakCfg.Entry("Level", "Reload Mode", "重载模式", null,
            "关卡重载模式\n保留方块和分数＝保方块并给全员补分；只保留方块＝分数清零\n补分在下次结算显示，未装本模组的房客同样生效",
            "Reload mode\nblocks+score = refill scores too; blocks only = scores reset\nrefill shows at the next settlement and also covers vanilla clients");
        // 预登记自动快捷键：不预登记则重启后首次打开本页前按键无效；渲染时会幂等再登记。
        SRApi.HotkeyAction("level.reload", "重载关卡", () => Level.ReloadLevel());
    }

    public void Initialize(IFeatureHost plugin) {
        SelfReg();
        // 对外能力：重载忙碌状态（预设据此避免并发重载）与清场（EX 的"清除地图对象"用）。
        SrCaps.LevelReloadBusy = () => _reloadBusy;
        FeatureRuntime.RecordRevoke(() => SrCaps.LevelReloadBusy = null);
        Action<List<Placeable>, string> __clear = ClearPlacedBlocks;
        SrCaps.LevelClearPlacedBlocks = __clear;
        FeatureRuntime.RecordRevoke(() => SrCaps.LevelClearPlacedBlocks = null);
        // 锁自愈 tick：预设加载会被残留的 level.reloadBusy 永久拦住，需定时放锁。
        try { SRApi.RegisterTick("关卡重载-锁自愈", TickReloadLock); }
        catch (Exception __tr) { SRApi.Guard.Log("关卡重载/注册锁自愈", __tr); }
        _reloadMode = plugin.Config.Bind<ReloadMode>("Level", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
            // CreateAndPatchAll 不自动发现带 [HarmonyPatch] 的独立类，必须显式注册。
        try { SRApi.RegisterPatch(typeof(Level)); }
        catch (Exception e) { SRApi.LogError("Level 补丁注册失败: " + e.Message); }
    }
}
}