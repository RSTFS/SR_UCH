// 关卡派对盒页（侧栏「关卡」第 1 页）：放弃道具 / 派对盒炸弹 / 派对盒（重抽·开箱·关闭）。
// 独立插件 PartyBoxCtrl.dll 内有等价实现，不依赖 SR；命名空间内 Level 会遮蔽游戏自带的 global::Level。
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
        private static readonly int[] BombTypeVals = new int[] { 0, 1, 2 }; // 派对盒炸弹种类下拉的取值（0 小 / 1 大 / 2 超级）

        public static void RenderPartyBox() {
        // 分区：道具（自动道具 + 放弃道具放在同一分区里）
        SRApi.Ctl.SectionTitle("- " + SRApi.T("道具", "Items") + " -",
            SRApi.T("道具：开箱自动选择，以及放弃道具。",
                    "Items: auto-pick one when the box opens, plus the game's native drop-piece key."));
        // 分区内显式排序：自动道具 → 自动道具延迟 → 放弃道具 → 跳过炸弹
        float __itemCol = Mathf.Max(SRApi.Ctl.Sc(180), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(240));
        ConfigEntryBase __autoEntry = SRApi.Ctl.FindEntry("Level", "Auto Party Item");
        GUILayout.Space(SRApi.Ctl.Sc(4));
        if (__autoEntry != null) SRApi.Ctl.LabeledRow(SRApi.T("自动道具", "Auto party item"),
            SRApi.T("开箱后自动随机挑一个\n与手动挑选同一条链路，服务端发放\n关闭则不自动挑", "Picks a random piece when the box opens\nSame path as picking by hand; the server hands it over\nOff = no auto pick"), __autoEntry, 120f);

        // 自动道具延迟：统一滑块行（0-3 秒，0.1 步进；格式与其它页面一致）
        ConfigEntryBase __dl = SRApi.Ctl.FindEntry("Level", "Auto Party Item Delay");
        if (__dl != null)
            SRApi.Ctl.RowSlider(SRApi.T("自动道具延迟", "Auto item delay"),
                SRApi.T("开箱后等多久再自动挑\n范围 0-3 秒，步进 0.1",
                        "how long to wait before picking\n0-3 seconds in 0.1 steps"),
                __dl, 0f, 3f, "0.0");
        // 跳过炸弹（自动道具下面）
        ConfigEntryBase __skip = SRApi.Ctl.FindEntry("Level", "Auto Party Item Skip Bomb");
        GUILayout.Space(SRApi.Ctl.Sc(4));
        if (__skip != null) SRApi.Ctl.LabeledRow(SRApi.T("跳过炸弹", "Skip bombs"),
            SRApi.T("自动挑道具时排除派对盒里的炸弹\n关闭后炸弹也可能被自动挑中\n只影响自动挑选", "Exclude party-box bombs when auto picking\nWhen off a bomb may be picked\nAuto pick only"), __skip, 120f);
        // 放弃道具（道具分区最下面）
        ConfigEntryBase __drop = SRApi.Ctl.FindEntry("Level", "Drop Held Piece");
        GUILayout.Space(SRApi.Ctl.Sc(4));
        if (__drop != null) SRApi.Ctl.LabeledRow(SRApi.T("放弃道具", "Drop held piece"),
            SRApi.T("派对阶段丢弃手上的道具\n请勿删除边界墙和出生方块，一经删除无法复原\n仅派对阶段、走游戏原生通道", "Discards the held piece during the pick phase\nDo not delete boundary walls or spawn blocks; they cannot be restored\nPick phase only, uses the game channel"), __drop, 120f);

        // 派对盒炸弹：对局内所有成员都发过炸弹！快捷短语才生成
        SRApi.Ctl.SectionTitle(SRApi.T("- 派对盒子炸弹 -", "- Party-box bomb -"));
        GUILayout.BeginHorizontal();
        ConfigEntryBase pbe = SRApi.Ctl.FindEntry("Level", "Party Bomb"); // Bind 在 Level 段，查 [EX] 段取不到
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("派对盒子炸弹", "Party-box bomb"), SRApi.T("房主开启后，对局内所有成员都发过炸弹！快捷短语时，往当前派对盒子塞入炸弹。", "Host on: when every member sends the Bomb quick-phrase, spawn a bomb in the current party box.")), pbe, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(40));
        if (pbe != null) SRApi.Ctl.RenderControl(pbe);
        GUILayout.Space(SRApi.Ctl.Sc(10));
        GUILayout.Label(SRApi.T("炸弹种类", "Bomb type"), SRApi.Ctl.Label, GUILayout.Height(SRApi.Ctl.Sc(26)));
        ConfigEntryBase pbtype = SRApi.Ctl.FindEntry("Level", "Party Bomb Type");
        if (pbtype != null) {
            // 三档：0 小 / 1 大 / 2 超级（对应游戏内 99abombmini / 99bomb / 99bbombmega）
            string[] opts = new[] { SRApi.T("小炸弹", "Small"), SRApi.T("大炸弹", "Big"), SRApi.T("超级炸弹", "Mega") };
            int curIdx = Mathf.Clamp((int)pbtype.BoxedValue, 0, 2);
            bool open; if (!SRApi.Ctl.EditOpen.TryGetValue(pbtype, out open)) open = false;
            int pbSel = SRApi.Ctl.ComboBox(pbtype, opts[curIdx], BombTypeVals, opts, ref open, SRApi.Ctl.Sc(150));
            if (pbSel >= 0) { SRApi.Ctl.SetValue(pbtype, BombTypeVals[pbSel]); SRApi.Ctl.EditOpen[pbtype] = false; }
            else SRApi.Ctl.EditOpen[pbtype] = open;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));

        SRApi.Ctl.SectionTitle(SRApi.T("- 派对盒（重抽 / 开箱 / 关闭）-", "- Party box -"));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyActionButton(SRApi.T("刷新派对盒", "Reroll + reopen box"), SRApi.T("完整重走一遍游戏自己的派对盒流程：按本轮权重重抽道具 + 重新开箱（全员可再选一次）", "Run the game own party-box flow again: reroll with this round weights and reopen the box so everyone can pick again"), LevelPartyBox.RefreshPartyBoxKeyEntry, SRApi.Ctl.Sc(150), true)) LevelPartyBox.RefreshPartyBox();
        GUILayout.Space(SRApi.Ctl.Sc(8));
        if (SRApi.Ctl.HotkeyActionButton(SRApi.T("关闭派对盒", "Close party box"), SRApi.T("结束派对盒选择，进入游玩阶段（走游戏原生相位切换，全员同步）", "End the party-box selection and enter the play phase (native phase change, synced)"), LevelPartyBox.ClosePartyBoxKeyEntry, SRApi.Ctl.Sc(150), true)) LevelPartyBox.ClosePartyBox();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(4));
        }

}

    // 派对盒炸弹：房主端统计对局内所有在线成员的炸弹！快捷短语（EmoteMeanings.EMOTE_Bomb，按枚举码识别，与语言无关）。
    // 全员都发过才往当前派对盒塞入一个可选取的炸弹，型号可配置（PartyBox.BombPrefab 的下标）。
    // PartyBomb / DropHeldPiece 两个功能见 Level.PartyBomb.cs。


[SrFeature("sr.levelpartybox", "LevelPartyBox", "1.0")]
public class LevelPartyBox : ITweak {
        private static ConfigEntry<KeyCode> _refreshPartyBoxKey;
        private static ConfigEntry<KeyCode> _closePartyBoxKey;

        // 派对盒正在重建/关闭的窗口（秒）：刷新或关闭后一小段时间内，自动道具不得动作，
        // 否则会对着已经被销毁的旧盒子取道具（表现为"自动道具和开关派对盒冲突"）。
        internal static float _closeFlushAt = -1f;      // 关闭派对盒的边沿补发时刻
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

        // 快捷键轮询：与页面按钮共用同一批动作
        private static void Tick() {
            TickCloseFlush();
            // 刷新/关闭快捷键已迁移到核心 RegisterKey（见 Initialize），这里只做每帧收尾。
        }

        // 刷新派对盒：完整重走游戏自己的流程（showPartyBox = SetupPartyBoxForRound(true) + ShowBox(IsSecondBox)）。
        // 房主端三步：广播销毁旧道具 -> showPartyBox() -> CallRpcShowPartyBox()，全员才能一致。
        // 边沿补发：上一帧复位了 showingPlacementWarning，这里再置位一次让游戏重新推进相位。
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
                BoxBusyUntil = Time.unscaledTime + BoxBusySeconds;   // 重建期间喂自动道具"别动"
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) return;   // 非派对模式：静默跳过（不弹提示）
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SRApi.Notify(SRApi.T("未找到派对盒", "Party box not found")); return; }
                // 权限判定走 SRApi.CanDoHost()：道具由房主端生成并需服务器广播，提示文案由 SR 统一给出。
                if (!SRApi.CanDoHost()) return;
                int oldPieces = DestroyPartyBoxPieces(pb); // 旧道具必须广播销毁：ChoosePieces 内部的 ClearPieces 只用 Object.Destroy
                System.Reflection.MethodInfo show = SRApi.Ref.Method(typeof(VersusControl), "showPartyBox", "关卡派对盒流程");
                if (show != null) {
                    show.Invoke(vc, null);
                    try { vc.CallRpcShowPartyBox(); } catch (System.Exception __ex) { SRApi.LogWarn("重新开箱(通知客户端)失败: " + __ex.Message); }
                    SRApi.Notify(SRApi.T("已重抽道具并重新开箱", "Rerolled the items and reopened the box"));
                } else {
                    FallbackRefresh(pb, oldPieces); // 兜底：找不到 showPartyBox 时手工走同样两步
                }
                // 若 Phase 仍不是 PLACE，选完道具放不下，需要让全员进入放置状态。
                ForcePlacePhase(vc);
            } catch (System.Exception e) { SRApi.LogWarn("刷新派对盒失败: " + e.Message); }
        }

        // 让全员进入建造/放置状态：优先调游戏原生入口 VersusControl.ToPlaceMode()（内含 StartPhaseEvent 与光标同步）。
        // 回退路径：房主把 nextPhase 设为 PLACE，并显式 CallRpcSetupPlacementCursors(false) 同步房客。
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
                // 房客同步：让各客户端也建立放置光标
                try {
                    System.Reflection.MethodInfo su = SRApi.Ref.Method(typeof(VersusControl), "SetupPlacementCursors", "关卡派对盒：同步放置光标");
                    if (su != null) su.Invoke(vc, null);
                    vc.CallRpcSetupPlacementCursors(false);
                } catch (System.Exception __s) { SRApi.LogWarn("同步放置光标失败: " + __s.Message); }
            } catch (System.Exception e) { SRApi.LogWarn("切换到放置阶段失败: " + e.Message); }
        }

        // 只重抽盒子里的道具：不重新开箱、不动相位。
        // 关闭派对盒：把原生放置计时推到结束，其余（广播、关盒、收/换光标、相位同步）全部交给游戏自己的状态机与 RPC。
        // 前置条件：需要服务器权限；原生要求 AreAllPiecesReady()，还有人没选完时不会推进相位（此时用「放弃道具」）。

        internal static void ClosePartyBox() {
            try {
                BoxBusyUntil = Time.unscaledTime + BoxBusySeconds;   // 关闭期间喂自动道具"别动"
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) return;   // 非派对模式：静默跳过（不弹提示）
                // 相位推进是服务器权限（原生 RPC 也只在房主侧发）。
                if (!SRApi.CanDoHost()) return;
                // placementTimer / showingPlacementWarning 是 VersusControl 的 protected 字段，必须走 SRApi.Ref.Field 并登记。
                System.Reflection.FieldInfo pt = SRApi.Ref.Field(typeof(VersusControl), "placementTimer",
                    "关卡派对盒：关闭（把原生放置计时推到结束）");
                System.Reflection.FieldInfo pw = SRApi.Ref.Field(typeof(VersusControl), "showingPlacementWarning",
                    "关卡派对盒：关闭（进入原生的放置结束提示窗口）");
                if (pt == null || pw == null) {
                    SRApi.Notify(SRApi.T("关闭派对盒失败（游戏成员不可用）", "Failed to close the party box (member unavailable)"));
                    return;
                }
                // 计时归零并进入警告窗口：下一帧游戏自己走完剩余原生流程。
                // 边沿触发：卡在 0 时（计时已是 0、警告位已是 true）再次写同样的值是空操作，
                // 游戏不会重新走相位切换 → "关闭派对盒失效"。先复位警告位，下一帧再置位。
                bool wasWarn = false;
                try { wasWarn = (bool)pw.GetValue(vc); } catch (Exception __pw) { SRApi.Guard.Log("派对盒/读警告位", __pw); }
                float wasTimer = 0f;
                try { wasTimer = (float)pt.GetValue(vc); } catch (Exception __pt) { SRApi.Guard.Log("派对盒/读计时", __pt); }
                pt.SetValue(vc, 0f);
                if (wasWarn || wasTimer <= 0.01f) {
                    pw.SetValue(vc, false);                  // 复位，制造一次 0→1 的边沿
                    _closeFlushAt = Time.unscaledTime + 0.05f;
                    _closeFlushVc = vc;
                } else {
                    pw.SetValue(vc, true);
                }
                SRApi.Notify(SRApi.T("已关闭派对盒", "Party box closed"));
            } catch (Exception __ex) { SRApi.LogWarn("关闭派对盒失败: " + __ex.Message); }
        }

        // 旧道具广播销毁：Object.Destroy 不通知客户端，必须走 NetworkServer.Destroy，否则客户端残留旧道具。
        // 返回销毁前的件数（list 已 Clear，之后再也读不到）。
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

        // 兜底：找不到 VersusControl.showPartyBox 时按同样两步执行（按件数重抽 + 开箱广播）。
        private static void FallbackRefresh(PartyBox pb, int amount) {
            try {
                if (amount <= 0) amount = 1; // 件数由调用方在销毁前传入（此处 pieces 已清空）
                pb.ChoosePieces(amount, null);
                ReopenPartyBox(pb);
                SRApi.Notify(SRApi.T("已重抽派对盒道具", "Rerolled the party box items"));
            } catch (System.Exception e) { SRApi.LogWarn("刷新派对盒(兜底)失败: " + e.Message); }
        }

        // 兜底开箱：PartyBox.ShowBox + 广播 MsgPartyBoxOpen（客户端 handleEvent -> OnBoxOpen -> ShowBox）
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

// 自动道具：派对盒开启后，替本地玩家自动随机挑一个道具（默认跳过炸弹）。
// 走游戏自己的 PiecePicked 消息，与手动挑选完全同一条链路，服务端负责把道具发到手上。
    // AutoPartyItem（自动道具）见 Level.AutoPartyItem.cs。


}
