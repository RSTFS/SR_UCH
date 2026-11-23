// 关卡派对盒页（侧栏「关卡」第 1 页）：放弃道具 / 派对盒炸弹 / 派对盒（重抽·开箱·关闭）。
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
namespace SR_UCH.Tweaks {
public partial class Level {
        private static readonly int[] BombTypeVals = new int[] { 0, 1, 2 }; // 炸弹种类取值：0 小 / 1 大 / 2 超级

        public static void RenderPartyBox() {
        // 分区：道具（自动道具 → 延迟 → 跳过炸弹 → 放弃道具）
        SRApi.Ctl.SectionTitle(SRApi.Tid("6fbf85dd33", "道具", "Items"),
            SRApi.Tid("dfac712ee7", "道具：开箱自动选择，以及放弃道具。", "Items: auto-pick one when the box opens, plus the game's native drop-piece key."));
        float __itemCol = Mathf.Max(SRApi.Ctl.Sc(180), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(240));
        ConfigEntryBase __autoEntry = SRApi.Ctl.FindEntry("Level", "Auto Party Item");
        GUILayout.Space(SRApi.Ctl.Sc(4));
        if (__autoEntry != null) SRApi.Ctl.LabeledRow(SRApi.Tid("e60484bbc2", "自动道具", "Auto party item"),
            SRApi.Tid("c93dcb2b6f", "开箱后自动随机挑一个\n与手动挑选同一条链路，服务端发放\n关闭则不自动挑", "Picks a random piece when the box opens\nSame path as picking by hand; the server hands it over\nOff = no auto pick"), __autoEntry, 120f);

        ConfigEntryBase __dl = SRApi.Ctl.FindEntry("Level", "Auto Party Item Delay");
        if (__dl != null)
            SRApi.Ctl.RowSlider(SRApi.Tid("ae84f6ae12", "自动道具延迟", "Auto item delay"),
                SRApi.Tid("7d6c889e5f", "开箱后等多久再自动挑\n范围 0-3 秒，步进 0.1", "how long to wait before picking\n0-3 seconds in 0.1 steps"),
                __dl, 0f, 3f, "0.0");
        ConfigEntryBase __skip = SRApi.Ctl.FindEntry("Level", "Auto Party Item Skip Bomb");
        GUILayout.Space(SRApi.Ctl.Sc(4));
        if (__skip != null) SRApi.Ctl.LabeledRow(SRApi.Tid("67dc3f299f", "跳过炸弹", "Skip bombs"),
            SRApi.Tid("4a0dc6301a", "自动挑道具时排除派对盒里的炸弹\n关闭后炸弹也可能被自动挑中\n只影响自动挑选", "Exclude party-box bombs when auto picking\nWhen off a bomb may be picked\nAuto pick only"), __skip, 120f);
        ConfigEntryBase __drop = SRApi.Ctl.FindEntry("Level", "Drop Held Piece");
        GUILayout.Space(SRApi.Ctl.Sc(4));
        if (__drop != null) SRApi.Ctl.LabeledRow(SRApi.Tid("7ada60f959", "放弃道具", "Drop held piece"),
            SRApi.Tid("223cdfd7d1", "派对阶段丢弃手上的道具\n请勿删除边界墙和出生方块，一经删除无法复原\n仅派对阶段、走游戏原生通道", "Discards the held piece during the pick phase\nDo not delete boundary walls or spawn blocks; they cannot be restored\nPick phase only, uses the game channel"), __drop, 120f);

        // 派对盒炸弹：对局内所有成员都发过炸弹！快捷短语才生成。
        SRApi.Ctl.SectionTitle(SRApi.Tid("e2dd43a39f", "派对盒子炸弹", "Party-box bomb"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase pbe = SRApi.Ctl.FindEntry("Level", "Party Bomb"); // Bind 在 Level 段
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.Tid("e2dd43a39f", "派对盒子炸弹", "Party-box bomb"), SRApi.Tid("9f3d60de4b", "房主开启后，对局内所有成员都发过炸弹！快捷短语时，往当前派对盒子塞入炸弹。", "Host on: when every member sends the Bomb quick-phrase, spawn a bomb in the current party box.")), pbe, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(40));
        if (pbe != null) SRApi.Ctl.RenderControl(pbe);
        GUILayout.Space(SRApi.Ctl.Sc(10));
        // RowCombo 自带标签列，无需另画标签（否则会重复）。
        ConfigEntryBase pbtype = SRApi.Ctl.FindEntry("Level", "Party Bomb Type");
        if (pbtype != null) {
            string[] opts = new[] { SRApi.Tid("1ca0cd63ca", "小炸弹", "Small"), SRApi.Tid("77b64e4ac0", "大炸弹", "Big"), SRApi.Tid("4ba40007f7", "超级炸弹", "Mega") };
            string[] vals = new[] { "0", "1", "2" };
            SRApi.Ctl.RowCombo(SRApi.Tid("d902fac458", "炸弹种类", "Bomb type"),
                SRApi.Tid("174677a16e", "塞进派对盒的是哪种炸弹\n小 / 大 / 超级，对应不同的爆炸范围", "which bomb goes into the party box\nsmall / big / mega, each with a different blast"),
                pbtype, vals, opts, SRApi.Ctl.Sc(150));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));

        SRApi.Ctl.SectionTitle(SRApi.Tid("5912bde55a", "派对盒（重抽 / 开箱 / 关闭）", "Party box"));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyActionButton(SRApi.Tid("7e84b60137", "刷新派对盒", "Reroll + reopen box"), SRApi.Tid("3bb6f53c55", "完整重走一遍游戏自己的派对盒流程：按本轮权重重抽道具 + 重新开箱（全员可再选一次）", "Run the game own party-box flow again: reroll with this round weights and reopen the box so everyone can pick again"), LevelPartyBox.RefreshPartyBoxKeyEntry, SRApi.Ctl.Sc(150), true)) LevelPartyBox.RefreshPartyBox();
        GUILayout.Space(SRApi.Ctl.Sc(8));
        if (SRApi.Ctl.HotkeyActionButton(SRApi.Tid("ca7d265142", "关闭派对盒", "Close party box"), SRApi.Tid("582d674546", "结束派对盒选择，进入游玩阶段（走游戏原生相位切换，全员同步）", "End the party-box selection and enter the play phase (native phase change, synced)"), LevelPartyBox.ClosePartyBoxKeyEntry, SRApi.Ctl.Sc(150), true)) LevelPartyBox.ClosePartyBox();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(4));
        }

}

    // 派对盒炸弹：房主端统计全员炸弹！快捷短语（按 EMOTE_Bomb 枚举码识别，与语言无关）。
    // 全员都发过才往当前派对盒塞入可选取炸弹，型号由 PartyBox.BombPrefab 下标决定；实现见 Level.PartyBomb.cs。


[SrFeature("sr.levelpartybox", "LevelPartyBox", "1.0")]
public class LevelPartyBox : ITweak {
        private static ConfigEntry<KeyCode> _refreshPartyBoxKey;
        private static ConfigEntry<KeyCode> _closePartyBoxKey;

        // 重建/关闭窗口（秒）：此期间自动道具不得动作，否则会对着已销毁的旧盒子取道具。
        internal static float _closeFlushAt = -1f;      // 关闭边沿补发时刻
        private static VersusControl _closeFlushVc;
        internal static float BoxBusyUntil;

        private const float BoxBusySeconds = 1.2f;

        public static ConfigEntry<KeyCode> RefreshPartyBoxKeyEntry { get { return _refreshPartyBoxKey; } }
        public static ConfigEntry<KeyCode> ClosePartyBoxKeyEntry { get { return _closePartyBoxKey; } }

        public void Initialize(IFeatureHost plugin) {
            try {
                _refreshPartyBoxKey = plugin.Config.Bind("Level", "Refresh Party Box Key", KeyCode.None,
                    "刷新派对盒完整流程的快捷键. 在关卡页的刷新派对盒按钮上右键就能绑, 再右键一次删掉. 按下就直接执行.");
                _closePartyBoxKey = plugin.Config.Bind("Level", "Close Party Box Key", KeyCode.None,
                    "关闭派对盒快捷键（结束派对盒选择、进入游玩阶段；按下即触发）");
                SRApi.RegisterKey("关卡-刷新派对盒", _refreshPartyBoxKey, "press", delegate {
                    try { if (!SRApi.GateMaster) return; RefreshPartyBox(); }
                    catch (Exception __rk) { SRApi.Guard.Log("派对盒/刷新键", __rk); }
                });
                SRApi.RegisterKey("关卡-关闭派对盒", _closePartyBoxKey, "press", delegate {
                    try { if (!SRApi.GateMaster) return; ClosePartyBox(); }
                    catch (Exception __ck) { SRApi.Guard.Log("派对盒/关闭键", __ck); }
                });
                SRApi.RegisterTick("KEY_PARTYBOX", Tick);
                // 反射成员登记：游戏改名时启动自检会告警。
                SRApi.Ref.Override("PartyBox.pieces", "关卡派对盒道具（清场/重抽）");
                SRApi.Ref.Override("VersusControl.showPartyBox", "关卡派对盒：完整流程");
            SRApi.Ref.Override("VersusControl.showingPlacementWarning", "派对盒：建造阶段警告判定");
                SRApi.Ref.Override("VersusControl.IsSecondBox", "关卡派对盒：第二次开箱");
                SRApi.Ref.Override("VersusControl.ToPlaceMode", "关卡派对盒：房客一起进建造状态");
                SRApi.Ref.Override("VersusControl.SetupPlacementCursors", "关卡派对盒：同步放置光标");
                SRApi.Ref.Override("GameControl.nextPhase", "关卡派对盒：结束放置阶段");
            } catch (Exception __ex) { SRApi.Guard.Log("关卡派对盒 初始化", __ex); }
        }

        // 每帧：与页面按钮共用同一批动作（快捷键已由核心 RegisterKey 处理）。
        private static void Tick() {
            TickCloseFlush();
        }

        // 刷新派对盒：重走游戏自己的流程（showPartyBox + ShowBox(IsSecondBox)）。
        // 房主三步：广播销毁旧道具 → showPartyBox() → CallRpcShowPartyBox()，全员才一致。
        // 边沿补发：上一帧复位了 showingPlacementWarning，这里再置位让游戏重新推进相位。
        private static void TickCloseFlush() {
            try {
                if (_closeFlushAt < 0f || Time.unscaledTime < _closeFlushAt) return;
                _closeFlushAt = -1f;
                VersusControl vc = _closeFlushVc; _closeFlushVc = null;
                if (vc == null) return;
                System.Reflection.FieldInfo pw = SRApi.Ref.Field(typeof(VersusControl), "showingPlacementWarning",
                    "关卡派对盒：关闭（边沿补发）");
                if (pw == null) return;
                pw.SetValue(vc, true);
                SRApi.LogInfo("[派对盒] 已补发关闭边沿（showingPlacementWarning=true）");
            } catch (Exception __ex) { SRApi.Guard.Log("派对盒/关闭边沿补发", __ex); }
        }

        public static void RefreshPartyBox() {
            try {
                BoxBusyUntil = Time.unscaledTime + BoxBusySeconds;   // 重建期间自动道具别动
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) return;   // 非派对模式静默跳过
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SRApi.NotifyT("未找到派对盒", "Party box not found"); return; }
                // 道具由房主生成并需服务器广播，提示文案由 SR 统一给出。
                if (!SRApi.CanDoHost()) return;
                int oldPieces = DestroyPartyBoxPieces(pb); // 旧道具必须广播销毁：内部 ClearPieces 只用 Object.Destroy
                System.Reflection.MethodInfo show = SRApi.Ref.Method(typeof(VersusControl), "showPartyBox", "关卡派对盒流程");
                if (show != null) {
                    show.Invoke(vc, null);
                    try { vc.CallRpcShowPartyBox(); } catch (System.Exception __ex) { SRApi.LogWarn("重新开箱(通知客户端)失败: " + __ex.Message); }
                    SRApi.NotifyT("已重抽道具并重新开箱", "Rerolled the items and reopened the box");
                } else {
                    FallbackRefresh(pb, oldPieces); // 兜底：手工走同样两步
                }
                // 若 Phase 不是 PLACE，选完道具放不下，须让全员进入放置状态。
                ForcePlacePhase(vc);
            } catch (System.Exception e) { SRApi.LogWarn("刷新派对盒失败: " + e.Message); }
        }

        // 让全员进入放置状态：优先调原生 ToPlaceMode()；回退为设 nextPhase=PLACE 并同步放置光标。
        private static void ForcePlacePhase(VersusControl vc) {
            try {
                if (vc == null) return;
                System.Reflection.MethodInfo toPlace = SRApi.Ref.Method(typeof(VersusControl), "ToPlaceMode", "关卡派对盒：房客一起进建造状态");
                if (toPlace != null) {
                    try { toPlace.Invoke(vc, null); return; } catch (System.Exception __t) { SRApi.LogWarn("ToPlaceMode 失败，回退: " + __t.Message); }
                }
                System.Reflection.FieldInfo np = SRApi.Ref.Field(typeof(GameControl), "nextPhase", "关卡派对盒：结束放置阶段");
                if (np != null) {
                    object cur = np.GetValue(vc);
                    if (cur == null || cur.ToString() != "PLACE") np.SetValue(vc, System.Enum.Parse(np.FieldType, "PLACE"));
                }
                try {
                    System.Reflection.MethodInfo su = SRApi.Ref.Method(typeof(VersusControl), "SetupPlacementCursors", "关卡派对盒：同步放置光标");
                    if (su != null) su.Invoke(vc, null);
                    vc.CallRpcSetupPlacementCursors(false);
                } catch (System.Exception __s) { SRApi.LogWarn("同步放置光标失败: " + __s.Message); }
            } catch (System.Exception e) { SRApi.LogWarn("切换到放置阶段失败: " + e.Message); }
        }

        // 关闭派对盒：把原生放置计时推到结束，其余（广播/关盒/光标/相位）交给游戏状态机与 RPC。
        // 需服务器权限；原生要求 AreAllPiecesReady()，有人没选完时不会推进（此时用放弃道具）。

        internal static void ClosePartyBox() {
            try {
                BoxBusyUntil = Time.unscaledTime + BoxBusySeconds;   // 关闭期间自动道具别动
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) return;   // 非派对模式静默跳过
                if (!SRApi.CanDoHost()) return;
                // 两字段是 VersusControl 的 protected 成员，须走 SRApi.Ref.Field 并登记。
                System.Reflection.FieldInfo pt = SRApi.Ref.Field(typeof(VersusControl), "placementTimer",
                    "关卡派对盒：关闭（把原生放置计时推到结束）");
                System.Reflection.FieldInfo pw = SRApi.Ref.Field(typeof(VersusControl), "showingPlacementWarning",
                    "关卡派对盒：关闭（进入原生的放置结束提示窗口）");
                if (pt == null || pw == null) {
                    SRApi.NotifyT("关闭派对盒失败（游戏成员不可用）", "Failed to close the party box (member unavailable)");
                    return;
                }
                // 计时归零并进入警告窗口；卡在 0 时重复写同值是空操作，故先复位警告位、下一帧再置位。
                bool wasWarn = false;
                try { wasWarn = (bool)pw.GetValue(vc); } catch (Exception __pw) { SRApi.Guard.Log("派对盒/读警告位", __pw); }
                float wasTimer = 0f;
                try { wasTimer = (float)pt.GetValue(vc); } catch (Exception __pt) { SRApi.Guard.Log("派对盒/读计时", __pt); }
                pt.SetValue(vc, 0f);
                if (wasWarn || wasTimer <= 0.01f) {
                    pw.SetValue(vc, false);                  // 制造 0→1 边沿
                    _closeFlushAt = Time.unscaledTime + 0.05f;
                    _closeFlushVc = vc;
                } else {
                    pw.SetValue(vc, true);
                }
                SRApi.NotifyT("已关闭派对盒", "Party box closed");
            } catch (Exception __ex) { SRApi.LogWarn("关闭派对盒失败: " + __ex.Message); }
        }

        // 旧道具须广播销毁（Object.Destroy 不通知客户端）；返回销毁前件数（list 已清空）。
        private static int DestroyPartyBoxPieces(PartyBox pb) {
            int count = 0;
            try {
                System.Reflection.FieldInfo f = SRApi.Ref.Field(typeof(PartyBox), "pieces", "关卡派对盒道具（清场/重抽）");
                System.Collections.IList list = f != null ? f.GetValue(pb) as System.Collections.IList : null;
                if (list == null) return 0;
                List<UnityEngine.GameObject> dead = new List<UnityEngine.GameObject>();
                foreach (object o in list) {
                    UnityEngine.Component c = o as UnityEngine.Component;
                    if (c != null && c.gameObject != null) dead.Add(c.gameObject);
                }
                for (int i = 0; i < dead.Count; i++) {
                    try { UnityEngine.Networking.NetworkServer.Destroy(dead[i]); }
                    catch (Exception __ex) { SRApi.LogWarn("刷新派对盒(销毁旧道具)失败: " + __ex.Message); }
                }
                list.Clear();
                count = dead.Count;
            } catch (Exception e) { SRApi.LogWarn("刷新派对盒(清理)失败: " + e.Message); }
            return count;
        }

        // 兜底：找不到 showPartyBox 时按同样两步执行（按件数重抽 + 开箱广播）。
        private static void FallbackRefresh(PartyBox pb, int amount) {
            try {
                if (amount <= 0) amount = 1; // 件数由调用方在销毁前传入
                pb.ChoosePieces(amount, null);
                ReopenPartyBox(pb);
                SRApi.NotifyT("已重抽派对盒道具", "Rerolled the party box items");
            } catch (System.Exception e) { SRApi.LogWarn("刷新派对盒(兜底)失败: " + e.Message); }
        }

        // 兜底开箱：PartyBox.ShowBox + 广播 MsgPartyBoxOpen。
        private static void ReopenPartyBox(PartyBox pb) {
            try {
                if (pb == null) return;
                bool extra = false;
                try {
                    VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                    System.Reflection.FieldInfo f = vc != null ? SRApi.Ref.Field(typeof(VersusControl), "IsSecondBox", "关卡派对盒流程") : null;
                    if (f != null) extra = (bool)f.GetValue(vc);
                } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                try { pb.ShowBox(extra); } catch (Exception __ex) { SRApi.LogWarn("重新开箱(本地)失败: " + __ex.Message); }
                try {
                    if (UnityEngine.Networking.NetworkServer.active) {
                        MsgPartyBoxOpen msg = new MsgPartyBoxOpen();
                        msg.IsOpen = true;
                        msg.isExtraBox = extra;
                        UnityEngine.Networking.NetworkServer.SendToAll(NetMsgTypes.PartyBoxOpen, msg);
                    }
                } catch (Exception __ex) { SRApi.LogWarn("重新开箱(广播)失败: " + __ex.Message); }
            } catch (System.Exception e) { SRApi.LogWarn("重新开箱失败: " + e.Message); }
        }
    }

// 自动道具：开箱后替本地玩家随机挑一个道具（默认跳过炸弹）；走 PiecePicked 消息，与手动同链路。


}
