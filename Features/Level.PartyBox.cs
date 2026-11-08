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
                SRApi.RegisterKey("关卡-刷新派对盒", _refreshPartyBoxKey, "press");
                SRApi.RegisterKey("关卡-关闭派对盒", _closePartyBoxKey, "press");
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
            try {
                if (!SRApi.GateMaster) return;
                if (SRApi.ComboKeyDown(_refreshPartyBoxKey)) RefreshPartyBox();
                if (SRApi.ComboKeyDown(_closePartyBoxKey)) ClosePartyBox();
            } catch (Exception __ex) { SRApi.Guard.Log("派对盒快捷键", __ex); }
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
                if (vc == null) { SRApi.Notify(SRApi.T("请先进入派对模式对局", "Not in a party match")); return; }
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
                if (vc == null) { SRApi.Notify(SRApi.T("请先进入派对模式对局", "Not in a party match")); return; }
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
[SrFeature("sr.autopartyitem", "AutoPartyItem", "1.0")]
public class AutoPartyItem : ITweak {
    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<float> _delay;
    private static ConfigEntry<bool> _skipBomb;

    private static System.Reflection.FieldInfo _fPieces;
    private static float _armedAt = -1f;      // 本次开箱开始计时的时刻
    // "本次开箱"用**光标进入挑选态的次数**做序号，而不是派对盒实例 ID：
    // 派对盒对象在整个大厅里是同一个实例、一轮一轮反复复用，用实例 ID 记账会让
    // 第一轮挑过之后"已挑"标记永远为真 —— 表现就是"第一次可以拿，之后都不行"。
    private static int _openSeq;
    private static int _pickedSeq = -1;
    private static bool _cursorWasPicking;
    private static float _nextAt = -999f;
    // 悬停挑选流程：先把本地光标挪到目标道具上（让游戏自己算出 hoveredPick），
    // 下一拍再调用游戏自己的 tryPickPiece 协程 —— 房主端只认游戏这条链路。
    private static PartyPickCursor _hpCur;
    private static PickableBlock _hpTarget;
    private static int _hpStage;          // 0=空闲 1=把光标挪到目标上 2=启动协程 3=等待验收
    private static Vector3 _hpSavedPos;   // 光标原位（挑完还回去，用户看不到位移）
    private static bool _hpMoved;
    private static float _hpAt = -1f;
    private static int _hpAttempts;

    private static void SelfReg() {
        SRApi.LocKey("Level", "Auto Party Item", "自动道具", null);
        SRApi.LocDesc("Level", "Auto Party Item", "自动道具\n派对盒开启后自动随机挑一个道具\n与手动挑选同一条链路，服务端发放", "Auto party item\npicks a random piece for you once the box opens\nsame path as picking by hand; the server hands it over");
        SRApi.LocKey("Level", "Auto Party Item Delay", "自动道具延迟", null);
        SRApi.LocDesc("Level", "Auto Party Item Delay", "自动道具延迟\n开箱后等多少秒再挑\n范围 0-3，步进 0.1", "Auto item delay\nseconds to wait after the box opens\nrange 0-3 in steps of 0.1");
        SRApi.LocKey("Level", "Auto Party Item Skip Bomb", "跳过炸弹", null);
        SRApi.LocDesc("Level", "Auto Party Item Skip Bomb", "跳过炸弹\n自动挑道具时排除派对盒里的炸弹\n关闭后炸弹也可能被自动挑中", "Skip bombs\nexcludes party-box bombs from the random pick\nwhen off a bomb may be picked");
    }

    public void Initialize(IFeatureHost plugin) {
        try {
            SelfReg();
            _enabled = plugin.Config.Bind("Level", "Auto Party Item", false,
                "自动道具：派对盒开启后自动随机挑一个道具（与手动挑选同一条链路）。");
            _delay = plugin.Config.Bind("Level", "Auto Party Item Delay", 0.5f,
                new ConfigDescription("自动道具延迟（0-3 秒，步进 0.1）：开箱后等多久再自动挑。", new AcceptableValueRange<float>(0f, 3f)));
            _skipBomb = plugin.Config.Bind("Level", "Auto Party Item Skip Bomb", false,
                "跳过炸弹：自动挑道具时不挑派对盒里的炸弹。");
            SRApi.RegisterTick("派对盒-自动道具", Tick);
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/初始化", __ex); }
    }

    private static void Tick() {
        try {
            TickHoverPick();   // 挑选流程每帧推进（它自己有节流），不能被下面的 0.15s 轮询挡住
            // 开箱序号：光标从"未挑选"变成"正在挑选"就是新的一次开箱。
            // 不能用派对盒实例 ID 记账（大厅里是同一个实例反复复用）。
            PartyPickCursor seen = LocalCursor();
            if (seen != null && !_cursorWasPicking) { _openSeq++; _armedAt = -1f; _hpAttempts = 0; }
            _cursorWasPicking = seen != null;

            if (_enabled == null || !_enabled.Value) { _armedAt = -1f; return; }
            if (!SRApi.GateMaster) return;
            if (Time.unscaledTime < _nextAt) return;
            // 派对盒正在刷新/关闭：本帧不动作，避免对着已被销毁的旧盒子取道具（冲突修复）
            if (Time.unscaledTime < LevelPartyBox.BoxBusyUntil) { _armedAt = -1f; return; }
            _nextAt = Time.unscaledTime + 0.15f;   // 轮询节流：不抢游戏自己的逻辑，也不每帧全场景找

            PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
            if (pb == null) { _armedAt = -1f; return; }
            bool active = false;
            try { active = pb.IsStillActive; } catch (Exception __ac) { SRApi.Guard.Log("自动道具/盒状态", __ac); }
            if (!active) { _armedAt = -1f; return; }

            PartyPickCursor cur = seen;
            if (cur == null) { _armedAt = -1f; return; }
            if (_pickedSeq == _openSeq) return;      // 本次开箱已经自动挑过
            if (_armedAt < 0f) { _armedAt = Time.unscaledTime; return; }
            float delay = _delay != null ? Mathf.Clamp(_delay.Value, 0f, 3f) : 0.5f;
            if (Time.unscaledTime - _armedAt < delay) return;

            List<PickableBlock> pieces = ReadPieces(pb);
            if (pieces == null || pieces.Count == 0) return;
            PickableBlock target = Choose(pb, pieces);
            if (target == null) return;
            // 只在这里标记"本次已挑"，具体成没成由 TickHoverPick 的验收阶段判定
            // （避免两条日志一真一假、误以为跑了两遍）。
            if (DoPick(cur, target)) _pickedSeq = _openSeq;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/轮询", __ex); }
    }

    // 本地玩家自己的派对盒光标：有权威、已启用、正处于挑选状态。
    private static PartyPickCursor LocalCursor() {
        try {
            PartyPickCursor[] cursors = UnityEngine.Object.FindObjectsOfType<PartyPickCursor>();
            if (cursors == null) return null;
            for (int i = 0; i < cursors.Length; i++) {
                PartyPickCursor c = cursors[i];
                if (c == null || !c.hasAuthority || !c.Enabled || !c.Picking) continue;
                return c;
            }
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/查找光标", __ex); }
        return null;
    }

    private static List<PickableBlock> ReadPieces(PartyBox pb) {
        try {
            if (_fPieces == null) _fPieces = SRApi.Ref.Field(typeof(PartyBox), "pieces", "派对盒自动道具（候选列表）");
            if (_fPieces == null) return null;
            return _fPieces.GetValue(pb) as List<PickableBlock>;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/读取候选", __ex); return null; }
    }

    private static PickableBlock Choose(PartyBox pb, List<PickableBlock> pieces) {
        bool skipBomb = _skipBomb == null || _skipBomb.Value;
        List<PickableBlock> cand = new List<PickableBlock>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++) {
            PickableBlock p = pieces[i];
            if (p == null) continue;
            if (skipBomb && IsBomb(pb, p)) continue;
            cand.Add(p);
        }
        if (cand.Count == 0) return null;   // 只剩炸弹且选择了跳过：放弃本次自动挑选
        return cand[UnityEngine.Random.Range(0, cand.Count)];
    }







    private static bool IsBomb(PartyBox pb, PickableBlock p) {
        try {
            if (pb.BombPrefab == null || p.placeablePrefab == null) return false;
            for (int i = 0; i < pb.BombPrefab.Length; i++) {
                if (pb.BombPrefab[i] != null && pb.BombPrefab[i] == p.placeablePrefab) return true;
            }
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/炸弹判定", __ex); }
        return false;
    }

    // 与游戏 PartyPickCursor.tryPickPiece 完全一致：记下选中的对象 -> 发 PiecePicked -> 冻结并关闭光标。
    // 严格照抄游戏 PartyPickCursor.tryPickPiece 的**每一步**（顺序也要一致）：
    //   Picking=false → 记 NetworkpickedPiece → （有权威才）发 PiecePicked → Freeze → Disable()
    // 之前在房客端失效，就是因为少了 Picking=false 且用了不同的 Disable 重载：
    // 房主端的校验只认"光标已结束挑选"的状态，状态不对就当作无效挑选丢掉。
    // 走游戏自己的挑选链路：光标必须先"悬停"在道具上（游戏靠碰撞体算 hoveredPick，
    // 并从它取 netIdValue）。直接发 PiecePicked 在房客端会被房主判为无效。
    private static bool DoPick(PartyPickCursor cur, PickableBlock target) {
        try {
            if (cur == null || target == null) return false;
            _hpCur = cur; _hpTarget = target; _hpStage = 1; _hpAt = Time.unscaledTime + 0.06f;
            return true;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/启动悬停挑选", __ex); return false; }
    }

    // 每帧推进挑选流程。优先"直接选择"（把目标写进 hoveredPick 后**同一瞬间**启动协程，
    // 中间不留任何等待，游戏没机会把它清掉）；失败才退回复刻手动挑选（挪光标让物理自己算悬停）。
    //
    // 以前"直接写字段"不生效的原因：写完等了一拍再启动协程，中间跑了一个物理帧，
    // 光标没压在道具上 → OnTriggerExit2D → ClearHoverPick 把字段清成了 null。
    private static void TickHoverPick() {
        try {
            if (_hpStage == 0 || _hpCur == null || _hpTarget == null) return;
            if (Time.unscaledTime < _hpAt) return;

            if (_hpStage == 1) {
                // 直接选择：清残留 → 写悬停 → 立刻启动协程（同一调用栈内）。
                bool started = false;
                try { _hpCur.NetworkpickedPiece = null; } catch (Exception __cp) { SRApi.Guard.Log("自动道具/清挑选记录", __cp); }
                System.Reflection.FieldInfo hp = null;
                try { hp = SRApi.Ref.Field(typeof(PartyPickCursor), "hoveredPick", "派对盒自动道具（直接选择）"); }
                catch (Exception __hf) { SRApi.Guard.Log("自动道具/取悬停字段", __hf); }
                if (hp != null) {
                    try { hp.SetValue(_hpCur, _hpTarget); } catch (Exception __hs) { SRApi.Guard.Log("自动道具/写悬停字段", __hs); }
                    started = StartPickCoroutine();
                }
                _hpStage = started ? 3 : 2;
                _hpAt = started ? Time.unscaledTime + 0.45f : Time.unscaledTime + 0.12f;
                return;
            }

            if (_hpStage == 2) {
                // 兜底：复刻手动挑选 —— 把光标挪到道具上，等物理帧把 hoveredPick 填上再启动协程。
                if (!_hpMoved && _hpCur != null && _hpTarget != null) {
                    _hpSavedPos = _hpCur.transform.position;
                    _hpMoved = true;
                    Vector3 p = _hpTarget.transform.position;
                    _hpCur.transform.position = new Vector3(p.x, p.y + 0.15f, p.z);
                    _hpAt = Time.unscaledTime + 0.12f;
                    return;
                }
                try { _hpCur.NetworkpickedPiece = null; } catch (Exception __cp2) { SRApi.Guard.Log("自动道具/清挑选记录", __cp2); }
                bool ok = StartPickCoroutine();
                _hpStage = 3;
                _hpAt = Time.unscaledTime + 0.45f;
                if (!ok) { _hpStage = 0; _hpCur = null; _hpTarget = null; }
                return;
            }

            if (_hpStage == 3) {
                bool ok = false;
                try {
                    PartyPickCursor c0 = _hpCur;
                    if (c0 != null) ok = !c0.Picking || c0.Pick != null;   // 协程成功会把 Picking 置 false
                } catch (Exception __ok) { SRApi.Guard.Log("自动道具/确认挑选结果", __ok); }
                if (ok) {
                    _hpAttempts = 0;
                    SRApi.LogInfo("[自动道具] 已挑选：" + (_hpTarget != null ? _hpTarget.name : "?"));
                } else if (_hpAttempts < 2) {
                    _hpAttempts++;
                    _pickedSeq = -1;                 // 允许本箱再挑一次
                    _armedAt = Time.unscaledTime;    // 重新计时延迟
                }
                RestoreCursorPos();
                try {
                    System.Reflection.FieldInfo hp2 = SRApi.Ref.Field(typeof(PartyPickCursor), "hoveredPick", "派对盒自动道具（清理伪造悬停）");
                    if (hp2 != null && _hpCur != null) hp2.SetValue(_hpCur, null);
                } catch (Exception __hc) { SRApi.Guard.Log("自动道具/清理悬停字段", __hc); }
                _hpStage = 0; _hpCur = null; _hpTarget = null;
            }
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/挑选推进", __ex); }
    }

    // 启动游戏自己的 tryPickPiece 协程（房主端只认这条链路）。
    private static bool StartPickCoroutine() {
        try {
            System.Reflection.MethodInfo mi = null;
            try { mi = SRApi.Ref.Method(typeof(PartyPickCursor), "tryPickPiece", "派对盒自动道具（走游戏挑选协程）"); }
            catch (Exception __rf) { SRApi.Guard.Log("自动道具/取挑选方法", __rf); }
            if (mi == null) { FallbackSendPick(_hpCur, _hpTarget); return true; }
            System.Collections.IEnumerator co = mi.Invoke(_hpCur, null) as System.Collections.IEnumerator;
            if (co == null) return false;
            _hpCur.StartCoroutine(co);
            return true;
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/启动协程", __ex); return false; }
    }

    private static void RestoreCursorPos() {
        if (!_hpMoved || _hpCur == null) { _hpMoved = false; return; }
        try { _hpCur.transform.position = _hpSavedPos; } catch { /* 静默：光标可能已销毁 */ }
        _hpMoved = false;
    }

    // 兜底：与以前相同的直接发消息（仅当反射拿不到协程时）
    private static void FallbackSendPick(PartyPickCursor cur, PickableBlock target) {
        try {
            if (cur == null || target == null) return;
            if (UnityEngine.Networking.NetworkManager.singleton == null || UnityEngine.Networking.NetworkManager.singleton.client == null) return;
            cur.Picking = false;
            cur.NetworkpickedPiece = UnityEngine.Networking.ClientScene.FindLocalObject(new UnityEngine.Networking.NetworkInstanceId(target.netIdValue));
            if (cur.hasAuthority) {
                MsgPiecePicked msg = new MsgPiecePicked();
                msg.PickableNetID = target.netIdValue;
                msg.PlayerNumber = cur.networkNumber;
                UnityEngine.Networking.NetworkManager.singleton.client.Send(NetMsgTypes.PiecePicked, msg);
            }
            cur.Freeze();
            cur.Disable();
        } catch (Exception __ex) { SRApi.Guard.Log("自动道具/兜底下发", __ex); }
    }
}

}
