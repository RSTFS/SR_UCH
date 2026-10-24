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
        // 放弃道具条目：排队阶段手上拿着道具时才有意义
        SRApi.RenderSectionEntries("Level", delegate (ConfigEntryBase e) {
            return e.Definition.Key == "Drop Held Piece";
        });

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
    [SrFeature("sr.partybomb", "PartyBomb", "1.0")]
    public class PartyBomb : ITweak {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _bombSize;
        public static bool Enabled = false;

        // 已发过炸弹！快捷消息的成员（networkNumber 去重）
        private static readonly HashSet<int> _bombSent = new HashSet<int>();


        public void Initialize(IFeatureHost plugin) {
            SRApi.RegisterDismissHook(delegate { _bombSent.Clear(); });
            // 本文件反射的游戏私有成员：必须在此登记，游戏改名时启动自检会告警。
            SRApi.Ref.Override("PartyBox.pieces", "派对盒炸弹（生成/清理）");
            _enabled = plugin.Config.Bind("Level", "Party Bomb", false,
                "派对盒子炸弹: 房主开启后, 对局里所有人都发过炸弹这个快捷短语, 才会往派对盒子里塞炸弹. 只有房主生效.");
            _bombSize = plugin.Config.Bind("Level", "Party Bomb Type", 0,
                "塞进去的是哪种炸弹, 填 PartyBox.BombPrefab 里的第几个, 从 0 开始. 不同下标对应盒子里不同大小的炸弹. 第一次生成的时候日志里会列出有哪些可选.");
            Enabled = _enabled.Value;
            _enabled.SettingChanged += (s, e) => Enabled = _enabled.Value;
            // 带 try/catch：单个补丁目标失效（游戏更新改方法名/签名）只丢本类功能，不影响其它功能。
            try { Harmony.CreateAndPatchAll(typeof(PartyBomb)); }
            catch (Exception e) { SRApi.LogError("派对盒炸弹 补丁注册失败: " + e.Message); }
            // networkNumber 会跨对局复用，必须在换场景（DismissHook）与对局开始（SceneHook）两处清理，
            // 否则新局同号玩家会被误判为已发过炸弹，提前生成且可叠加。
            SRApi.RegisterSceneHook(delegate { _bombSent.Clear(); });
        }

        // 快捷短语都走 ChatDisplay.DisplayNewMessage，EmoteType 保留原枚举码；房主端收集每位成员的 EMOTE_Bomb。
        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage", new Type[] { typeof(ChatMessageDetails) })]
        [HarmonyPrefix]
        static void OnBombEmote(ChatMessageDetails chatMessageDetails) {
            if (!SRApi.GateMaster || !Enabled) return;
            try {
                if (chatMessageDetails.EmoteType != EmoteMeanings.EMOTE_Bomb) return;
                // 仅服务器端统计/生成：炸弹需 NetworkServer.Spawn 广播全员（SRApi.HasServer = NetworkServer.active）。
                if (!SRApi.HasServer) return;
                List<int> online = OnlinePlayerNumbers();
                if (online.Count == 0) return;
                _bombSent.Add(chatMessageDetails.NetworkNumber);
                bool allSent = true;
                foreach (int n in online) {
                    if (!_bombSent.Contains(n)) { allSent = false; break; }
                }
                if (!allSent) {
                    SRApi.LogInfo("[PartyBomb] 已收到 " + _bombSent.Count + "/" + online.Count + " 名成员的炸弹！快捷消息");
                    return;
                }
                _bombSent.Clear(); // 触发一次后清空，等待下一轮
                SRApi.LogInfo("[PartyBomb] 全员已发炸弹！，生成炸弹（型号 #" + BombSize + "）");
                AddBombToPartyBox();
            } catch (Exception e) {
                SRApi.LogWarn("[PartyBomb] 检测失败: " + e.Message);
            }
        }

        // 对局在线成员的 networkNumber
        static List<int> OnlinePlayerNumbers() {
            List<int> list = new List<int>();
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                        NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                        list.Add(info.NetworkNumber);
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("统计在线玩家(PlayerTracker)", __ex); }
            if (list.Count == 0) {
                try {
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.PlayerCharacter == null) continue;
                        int n = p.PlayerCharacter.networkNumber;
                        if (!list.Contains(n)) list.Add(n);
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("统计在线玩家(PlayerManager)", __ex); }
            }
            return list;
        }

        static int BombSize { get { return _bombSize != null ? Mathf.Clamp(_bombSize.Value, 0, 2) : 0; } }

        // 往当前派对盒塞入一个炸弹（与 PartyBox.ChoosePieces 中 Spawn 单个 piece 的做法一致）。
        static void AddBombToPartyBox() {
            try {
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SRApi.LogWarn("[PartyBomb] 未找到派对盒子"); return; }
                if (pb.BombPrefab == null || pb.BombPrefab.Length == 0) { SRApi.LogWarn("[PartyBomb] 派对盒子无炸弹预制体"); return; }
                Placeable bombPlaceable = PickBombPlaceable(pb, BombSize);
                if (bombPlaceable == null || bombPlaceable.PickableBlock == null) { SRApi.LogWarn("[PartyBomb] 炸弹档 " + BombSize + " 无 PickableBlock"); return; }

                // pieces 是 private，必须走 SRApi.Ref.Field 反射获取后加入
                var f = SRApi.Ref.Field(typeof(PartyBox), "pieces", "派对盒炸弹（生成/清理）");
                if (f == null) return;
                IList pieces = f.GetValue(pb) as IList;
                if (pieces == null) return;

                PickableBlock piece = UnityEngine.Object.Instantiate(bombPlaceable.PickableBlock);
                pieces.Add(piece);

                piece.transform.SetParent(pb.transform, false);
                try { piece.ChangeArtLayer("Background 2"); } catch (Exception __ex) { SRApi.Guard.Log("设置炸弹美术层", __ex); }
                foreach (Transform t in piece.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
                float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float rad = Mathf.Max(0.1f, pb.PlacementRadius * 0.8f);
                piece.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, Mathf.Sin(ang) * rad, 0f);
                piece.NetworkUseStartPosition = true;
                piece.NetworkStartPosition = piece.transform.localPosition; // Start() 会用 StartPosition 覆盖 localPosition
                piece.FindPartyBox = true; // 让 piece 找到并跟随派对盒（同游戏自身 spawn）
                try { piece.setInitialScale(GameSettings.GetInstance().partyBoxItemScale); } catch (Exception __ex) { SRApi.Guard.Log("设置炸弹初始缩放", __ex); }
                piece.InPartybox = true;
                piece.Enable();
                NetworkServer.Spawn(piece.gameObject);

                SRApi.LogInfo("[PartyBomb] 已向派对盒子塞入炸弹 (" + BombName(BombSize) + ", " + piece.name + ")");
            } catch (Exception e) {
                SRApi.LogWarn("[PartyBomb] 加炸弹失败: " + e.Message);
            }
        }

        // 炸弹三档名称：0 小 / 1 大 / 2 超级
        static string BombName(int size) {
            switch (size) {
                case 0: return "小炸弹";
                case 2: return "超级炸弹";
                default: return "大炸弹";
            }
        }

        // 按档从 PartyBox.BombPrefab 里挑炸弹预制体（按名字：mini / bomb / mega）。
        static Placeable PickBombPlaceable(PartyBox pb, int size) {
            Placeable[] arr = pb.BombPrefab;
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == null) continue;
                string n = arr[i].name.ToLowerInvariant();
                if (size == 0 && n.Contains("mini")) return arr[i];
                if (size == 2 && n.Contains("mega")) return arr[i];
            }
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == null) continue;
                string n = arr[i].name.ToLowerInvariant();
                if (size == 1 && n.Contains("bomb") && !n.Contains("mini") && !n.Contains("mega")) return arr[i];
            }
            // 按名字未匹配到型号时退回按索引取并告警（选“小炸弹”可能拿到普通炸弹）。
            SRApi.LogWarn("[派对盒炸弹] 按名字未匹配到型号（size=" + size + "），已退回按索引取第 " + Mathf.Clamp(size, 0, arr.Length - 1) + " 个");
            int idx = Mathf.Clamp(size, 0, arr.Length - 1);
            return arr[idx];
        }
    }

    // 放弃道具：必须在排队阶段手上有道具时使用，走游戏原生的 Command 上行链（CallCmdClearPiece -> CmdClearPiece ->
    // RpcClearPiece 全员执行），联机同步天然正确；自己拼 SetPiece(null)+Destroy 会导致别人手上仍留着方块。
    [SrFeature("sr.dropheldpiece", "DropHeldPiece", "1.0")]
    public class DropHeldPiece : ITweak {
        private static ConfigEntry<KeyCode> _key;

        public void Initialize(IFeatureHost plugin) {
            try {
                SRApi.LocKey("Level", "Drop Held Piece", "放弃道具", "Drop held piece");
                SRApi.LocDesc("Level", "Drop Held Piece", "放弃道具快捷键\n排队阶段丢弃手上的道具，走游戏原生通道，全员可见\n不绑键则不生效；未绑定。", "drop held piece key\ndiscards the held piece during the queue phase through the game own channel; visible to everyone\ninactive while unbound; unbound");
                _key = plugin.Config.Bind("Level", "Drop Held Piece", KeyCode.None,
                    "按这个键就把手上拿着的那块丢掉（走游戏原生通道, 全员可见）, 填 None 就是不绑键.");
                SRApi.RegisterKey("关卡-放弃道具", _key, "press");
                SRApi.RegisterTick("放弃道具", Tick);
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具 初始化", __ex); }
        }

        private static void Tick() {
            if (_key == null || _key.Value == KeyCode.None) return;
            if (!SRApi.ComboKeyDown(_key)) return;
            Perform();
        }

        // 执行放弃道具；门控集中在此处。
        public static void Perform() {
            try {
                if (!SRApi.GateMaster) return;
                PiecePlacementCursor c = FindCursor();
                if (c == null) { SRApi.Notify(SRApi.T("未找到放置光标", "Placement cursor not found")); return; }
                if (c.Piece == null) { SRApi.Notify(SRApi.T("手上没有方块", "No piece in hand")); return; }
                if (!CallNativeRemoveUnplaced()) {
                    SRApi.Notify(SRApi.T("放弃失败", "Failed to drop the piece"));
                    SRApi.LogWarn("[放弃道具] 原生放弃接口不可用");
                    return;
                }
                SRApi.Notify(SRApi.T("已放弃当前方块", "Piece dropped"));
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具", __ex); }
        }

        // 本机的放置光标：CursorInstance 挂在 LobbyPlayer 上（Player 上只有 AssociatedLobbyPlayer），
        // 故经 LobbyManager.lobbySlots 筛 IsLocalPlayer。
        private static PiecePlacementCursor FindCursor() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.lobbySlots != null) {
                    NetworkLobbyPlayer[] slots = lm.lobbySlots;
                    for (int i = 0; i < slots.Length; i++) {
                        LobbyPlayer lp = slots[i] as LobbyPlayer;
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        PiecePlacementCursor ppc = lp.CursorInstance as PiecePlacementCursor;
                        if (ppc != null) return ppc;
                    }
                }
                // 退路：单机或 lobbySlots 尚未生成时取场上任一 PiecePlacementCursor
                PiecePlacementCursor[] all = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (all != null && all.Length > 0) return all[0];
            } catch (Exception __ex) { SRApi.Guard.Log("找放置光标", __ex); }
            return null;
        }

        // 放弃道具必须走游戏原生的 Command 上行链（CallCmdClearPiece -> CmdClearPiece -> RpcClearPiece 全员执行），
        // 房客调用也全场可见；VersusControl.CallRpcRemoveUnplacedObjects 是服务器下行广播，房客调用会被第一行拦掉。
        private static bool CallNativeRemoveUnplaced() {
            try {
                PiecePlacementCursor c = FindCursor();
                if (c == null) return false;
                if (c.Piece == null) return false;
                int myNum = c.networkNumber;

                // CallCmdClearPiece 之外必须补发上行 MsgPiecePlaced{PieceID=0}：服务端据此把 RemainingPlacements 清零并发
                // PlacementSkippedEvent -> Cursor.Hide()，否则光标残留、放置阶段永不结束（整局卡住）。
                bool sent = false;
                try { c.CallCmdClearPiece(); sent = true; }
                catch (Exception __ce) { SRApi.Guard.Log("放弃道具(Command 上行)", __ce); return false; }

                try {
                    MsgPiecePlaced skip = new MsgPiecePlaced();
                    skip.PlayerNumber = myNum;
                    skip.PiecePosition = Vector3.zero;
                    skip.PieceScale = Vector3.zero;
                    skip.PieceRotation = Quaternion.identity;
                    skip.PieceID = 0; // <=0：客户端据此发 PlacementSkippedEvent，服务端忽略放置
                    LobbyManager.instance.client.Send(NetMsgTypes.PiecePlaced, skip);
                } catch (Exception __se) { SRApi.Guard.Log("放弃道具(上报跳过)", __se); return sent; }
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具（原生通道）", __ex); return false; }
        }
    }

// 派对盒三个动作（重抽 / 开箱 / 关闭）的实现，页面由 Level.RenderPartyBox 渲染。
[SrFeature("sr.levelpartybox", "LevelPartyBox", "1.0")]
public class LevelPartyBox : ITweak {
        private static ConfigEntry<KeyCode> _refreshPartyBoxKey;
        private static ConfigEntry<KeyCode> _closePartyBoxKey;

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
                SRApi.RegisterTick("派对盒快捷键", Tick);
                // 反射成员登记：游戏改名时启动自检会告警。
                SRApi.Ref.Override("PartyBox.pieces", "关卡派对盒道具（清场/重抽）");
                SRApi.Ref.Override("VersusControl.showPartyBox", "关卡派对盒：完整流程");
                SRApi.Ref.Override("VersusControl.IsSecondBox", "关卡派对盒：第二次开箱");
                SRApi.Ref.Override("VersusControl.ToPlaceMode", "关卡派对盒：房客一起进建造状态");
                SRApi.Ref.Override("VersusControl.SetupPlacementCursors", "关卡派对盒：同步放置光标");
                SRApi.Ref.Override("GameControl.nextPhase", "关卡派对盒：结束放置阶段");
            } catch (Exception __ex) { SRApi.Guard.Log("关卡派对盒 初始化", __ex); }
        }

        // 快捷键轮询：与页面按钮共用同一批动作
        private static void Tick() {
            try {
                if (!SRApi.GateMaster) return;
                if (SRApi.ComboKeyDown(_refreshPartyBoxKey)) RefreshPartyBox();
                if (SRApi.ComboKeyDown(_closePartyBoxKey)) ClosePartyBox();
            } catch (Exception __ex) { SRApi.Guard.Log("派对盒快捷键", __ex); }
        }

        // 刷新派对盒：完整重走游戏自己的流程（showPartyBox = SetupPartyBoxForRound(true) + ShowBox(IsSecondBox)）。
        // 房主端三步：广播销毁旧道具 -> showPartyBox() -> CallRpcShowPartyBox()，全员才能一致。
        public static void RefreshPartyBox() {
            try {
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
                pt.SetValue(vc, 0f);
                pw.SetValue(vc, true);
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
                } catch { }
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
}