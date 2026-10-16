// 关卡 · 派对盒页（侧栏「关卡」栏目第 1 页）。放弃道具 / 派对盒炸弹 / 派对盒（重抽·开箱·关闭）。
// **命名提醒**见 Level.cs 顶部：本命名空间里的 Level 会遮住游戏自带的 global::Level。
// 说明：独立插件 PartyBoxCtrl.dll 里另有一份等价实现，那份不依赖 SR，供不装 SR 的人用。
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
namespace SR_UCH.Tweaks {
// 派对盒页。放弃道具 / 派对盒子炸弹 / 派对盒（重抽·开箱·关闭）都在这一页。
public partial class Level {
        private static readonly int[] BombTypeVals = new int[] { 0, 1, 2 };   // **R412**：派对盒炸弹种类下拉的 vals（0小/1大/2超级）

        public static void RenderPartyBox() {
        // 放弃道具：本页第一项（排队阶段手上拿着道具时才有意义）
        SR.RenderSectionEntries("Level", delegate (ConfigEntryBase e) {
            return e.Definition.Key == "Drop Held Piece";
        });

        //派对盒子炸弹：对局内所有成员都发过炸弹！快捷短语才生成
        GUILayout.Label(SR.T("- 派对盒子炸弹 -", "- Party-box bomb -"), SR.Ctl.SecHeader);
        //选择框 + 炸弹种类同一行（种类紧挨在选择框右边）
        GUILayout.BeginHorizontal();
        ConfigEntryBase pbe = SR.Ctl.FindEntry("Level", "Party Bomb"); //PartyBomb 的 Bind 在 Level 段，查 [EX] 段取不到（勾选框写不回去）
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("派对盒子炸弹", "Party-box bomb"), SR.T("房主开启后，对局内所有成员都发过炸弹！快捷短语时，往当前派对盒子塞入炸弹。", "Host on: when every member sends the Bomb quick-phrase, spawn a bomb in the current party box.")), pbe, SR.Ctl.Sc(140), SR.Ctl.Sc(40));
        if (pbe != null) SR.Ctl.RenderControl(pbe);
        GUILayout.Space(SR.Ctl.Sc(10));
        GUILayout.Label(SR.T("炸弹种类", "Bomb type"), SR.Ctl.Label, GUILayout.Height(SR.Ctl.Sc(26)));   // #2 去掉写死宽度（64px 装不下 4 个中文字）-> 自适应
        ConfigEntryBase pbtype = SR.Ctl.FindEntry("Level", "Party Bomb Type"); //同上：取不到则整行下拉框不显示
        if (pbtype != null) {
            //三档：0小 / 1大 / 2超级（对应游戏内 99abombmini / 99bomb / 99bbombmega）
            string[] opts = new[] { SR.T("小炸弹", "Small"), SR.T("大炸弹", "Big"), SR.T("超级炸弹", "Mega") };
            int curIdx = Mathf.Clamp((int)pbtype.BoxedValue, 0, 2);
            bool open; if (!SR.Ctl.EditOpen.TryGetValue(pbtype, out open)) open = false;
            int pbSel = SR.Ctl.ComboBox(pbtype, opts[curIdx], BombTypeVals, opts, ref open, SR.Ctl.Sc(150));
            if (pbSel >= 0) { SR.Ctl.SetValue(pbtype, BombTypeVals[pbSel]); SR.Ctl.EditOpen[pbtype] = false; }
            else SR.Ctl.EditOpen[pbtype] = open;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));

        GUILayout.Label(SR.T("- 派对盒（重抽 / 开箱 / 关闭）-", "- Party box -"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        if (SR.Ctl.HotkeyActionButton(SR.T("刷新派对盒", "Reroll + reopen box"), SR.T("完整重走一遍游戏自己的派对盒流程：按本轮权重重抽道具 + 重新开箱（全员可再选一次）", "Run the game own party-box flow again: reroll with this round weights and reopen the box so everyone can pick again"), LevelPartyBox.RefreshPartyBoxKeyEntry, SR.Ctl.Sc(150), true)) LevelPartyBox.RefreshPartyBox();
        GUILayout.Space(SR.Ctl.Sc(8));
        if (SR.Ctl.HotkeyActionButton(SR.T("刷新道具", "Reroll items"), SR.T("只重抽派对盒里的道具，不重新开箱、不切换相位", "Only reroll the items; no reopen and no phase change"), LevelPartyBox.RefreshPiecesKeyEntry, SR.Ctl.Sc(150), true)) LevelPartyBox.RefreshPartyBoxPieces();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(6));
        GUILayout.BeginHorizontal();
        if (SR.Ctl.HotkeyActionButton(SR.T("关闭派对盒", "Close party box"), SR.T("结束派对盒选择，进入游玩阶段（走游戏原生相位切换，全员同步）", "End the party-box selection and enter the play phase (native phase change, synced)"), LevelPartyBox.ClosePartyBoxKeyEntry, SR.Ctl.Sc(150), true)) LevelPartyBox.ClosePartyBox();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));
        }
}

    //派对盒子炸弹：房主端统计对局内所有在线成员的炸弹！快捷短语
    //（EmoteMeanings.EMOTE_Bomb，按枚举码识别，与多语言/文本无关），全员都发过才往当前
    //派对盒子塞入一个可选取的炸弹；型号可配置（PartyBox.BombPrefab 的第几个）。
    // **实验功能**：动态改 PartyBox 私有 pieces + 网络同步，需游戏内验证。
    public class PartyBomb : ITweak {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _bombSize;
        public static bool Enabled = false;

        //已发送过炸弹！快捷消息的成员（networkNumber 去重）
        private static readonly HashSet<int> _bombSent = new HashSet<int>();


        public void Initialize(IFeatureHost plugin) {
            SR.RegisterDismissHook(delegate { _bombSent.Clear(); });
//本文件反射的游戏私有成员（游戏更新改名时：启动自检会在日志里告警；改名需等新版模组适配）
            SR.RefOverride("PartyBox.pieces", "派对盒炸弹（生成/清理）");
            _enabled = plugin.Config.Bind("Level", "Party Bomb", false,
                "派对盒子炸弹: 房主开启后, 对局里所有人都发过炸弹这个快捷短语, 才会往派对盒子里塞炸弹. 只有房主生效.");
            _bombSize = plugin.Config.Bind("Level", "Party Bomb Type", 0,
                "塞进去的是哪种炸弹, 填 PartyBox.BombPrefab 里的第几个, 从 0 开始. 不同下标对应盒子里不同大小的炸弹. 第一次生成的时候日志里会列出有哪些可选.");
            Enabled = _enabled.Value;
            _enabled.SettingChanged += (s, e) => Enabled = _enabled.Value;
            //带 try/catch：单个补丁目标失效（游戏更新改方法名/签名）只丢本类功能，不影响其它功能。
            try { Harmony.CreateAndPatchAll(typeof(PartyBomb)); }
            catch (Exception e) { SR.LogError("派对盒炸弹 补丁注册失败: " + e.Message); }
            //换场景/换对局清空已发过炸弹记录：networkNumber 会跨对局复用，
            //残留会让新局里同号玩家被当成"已发"-> 不必全员发过就提前塞炸弹。
            // R396 S8：_bombSent 的清理时机。原来**只有** activeSceneChanged（换场景），
            //  但注释自己说 networkNumber 会跨对局复用, 同一场景内连续两局（派对常在同一场景重开）
            //  不触发该事件，残留 id 会让新局同号玩家被误判"已发过炸弹" -> 炸弹提前生成
            //  （AddBombToPartyBox 无次数上限，可叠加）。
            //  补一个对局开始的清理点：游戏自己发 StartPhaseEvent.START（反编译 125576），
            //  用 SR 的场景钩子接（自带按名去重，不会有重复注册问题）。
            SR.RegisterSceneHook(delegate { _bombSent.Clear(); });
        }

        //快捷短语都走 ChatDisplay.DisplayNewMessage，EmoteType 保留原枚举码；房主端收集每位成员的 EMOTE_Bomb。
        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage", new Type[] { typeof(ChatMessageDetails) })]
        [HarmonyPrefix]
        static void OnBombEmote(ChatMessageDetails chatMessageDetails) {
            if (!SR.GateMaster || !Enabled) return;
            try {
                if (chatMessageDetails.EmoteType != EmoteMeanings.EMOTE_Bomb) return;
                //仅服务器（房主）端统计/生成：炸弹需 NetworkServer.Spawn 广播全员；统一走 SR.HasServer（= NetworkServer.active）
                if (!SR.HasServer) return;
                List<int> online = OnlinePlayerNumbers();
                if (online.Count == 0) return;
                _bombSent.Add(chatMessageDetails.NetworkNumber);
                bool allSent = true;
                foreach (int n in online) {
                    if (!_bombSent.Contains(n)) { allSent = false; break; }
                }
                if (!allSent) {
                    SR.LogInfo("[PartyBomb] 已收到 " + _bombSent.Count + "/" + online.Count + " 名成员的炸弹！快捷消息");
                    return;
                }
                _bombSent.Clear(); //触发一次后清空，等待下一轮全员再发
                SR.LogInfo("[PartyBomb] 全员已发炸弹！，生成炸弹（型号 #" + BombSize + "）");
                AddBombToPartyBox();
            } catch (Exception e) {
                SR.LogWarn("[PartyBomb] 检测失败: " + e.Message);
            }
        }

        //对局在线成员 networkNumber
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
            } catch (Exception __ex) { SR.Guard.Log("统计在线玩家(PlayerTracker)", __ex); }
            if (list.Count == 0) {
                try {
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.PlayerCharacter == null) continue;
                        int n = p.PlayerCharacter.networkNumber;
                        if (!list.Contains(n)) list.Add(n);
                    }
                } catch (Exception __ex) { SR.Guard.Log("统计在线玩家(PlayerManager)", __ex); }
            }
            return list;
        }

        static int BombSize { get { return _bombSize != null ? Mathf.Clamp(_bombSize.Value, 0, 2) : 0; } }

        //往当前派对盒子塞入一个炸弹（参考 PartyBox.ChoosePieces 里 Spawn 单个 piece 的做法）
        static void AddBombToPartyBox() {
            try {
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SR.LogWarn("[PartyBomb] 未找到派对盒子"); return; }
                if (pb.BombPrefab == null || pb.BombPrefab.Length == 0) { SR.LogWarn("[PartyBomb] 派对盒子无炸弹预制体"); return; }
                Placeable bombPlaceable = PickBombPlaceable(pb, BombSize);
                if (bombPlaceable == null || bombPlaceable.PickableBlock == null) { SR.LogWarn("[PartyBomb] 炸弹档 " + BombSize + " 无 PickableBlock"); return; }

                //pieces 是 private：反射获取后加入
                var f = SR.RefField(typeof(PartyBox), "pieces", "派对盒炸弹（生成/清理）");
                if (f == null) return;
                IList pieces = f.GetValue(pb) as IList;
                if (pieces == null) return;

                PickableBlock piece = UnityEngine.Object.Instantiate(bombPlaceable.PickableBlock);
                pieces.Add(piece);

                //设 parent / 美术层 / Layer，并在圆周半径内随机取位置
                piece.transform.SetParent(pb.transform, false);
                try { piece.ChangeArtLayer("Background 2"); } catch (Exception __ex) { SR.Guard.Log("设置炸弹美术层", __ex); }
                foreach (Transform t in piece.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
                float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float rad = Mathf.Max(0.1f, pb.PlacementRadius * 0.8f);
                piece.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, Mathf.Sin(ang) * rad, 0f);
                piece.NetworkUseStartPosition = true;
                piece.NetworkStartPosition = piece.transform.localPosition; //Start() 会用 StartPosition 覆盖 localPosition
                piece.FindPartyBox = true; //让 piece 找到并跟随派对盒子（同游戏自身 spawn）
                try { piece.setInitialScale(GameSettings.GetInstance().partyBoxItemScale); } catch (Exception __ex) { SR.Guard.Log("设置炸弹初始缩放", __ex); }
                piece.InPartybox = true;
                piece.Enable();
                NetworkServer.Spawn(piece.gameObject);

                SR.LogInfo("[PartyBomb] 已向派对盒子塞入炸弹 (" + BombName(BombSize) + ", " + piece.name + ")");
            } catch (Exception e) {
                SR.LogWarn("[PartyBomb] 加炸弹失败: " + e.Message);
            }
        }

        //炸弹三档名称：0 小 / 1 大 / 2 超级
        static string BombName(int size) {
            switch (size) {
                case 0: return "小炸弹";
                case 2: return "超级炸弹";
                default: return "大炸弹";
            }
        }

        //按档从 PartyBox.BombPrefab 里挑对应炸弹预制体（按名字：mini / 普通 bomb / mega）
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
            // **R396 S23**：按名字三个循环都没匹配上（换预制体命名/改版本），
            //  原来静默按索引兜底, size=0 而没有名字含 "mini" 的预制体时会返回 arr[0]（普通炸弹）
            //  -> 玩家选"小炸弹"实际拿到大炸弹，且日志无任何提示。补一条告警便于排查。
            SR.LogWarn("[派对盒炸弹] 按名字未匹配到型号（size=" + size + "），已退回按索引取第 " + Mathf.Clamp(size, 0, arr.Length - 1) + " 个");
            int idx = Mathf.Clamp(size, 0, arr.Length - 1);
            return arr[idx];
        }
    }

    // 放弃道具。需求很简单：派对盒里选好了道具、进了建造阶段，又不想放了。
    // 做法是调游戏自己的 PiecePlacementCursor.ClearCurrentPiece()（private，73021）：
    //   它内部 if (hasAuthority) { CallCmdClearPiece(); 播音效 } -> CmdClearPiece -> **RpcClearPiece**，
    //   由**全员**执行（已放下的先发 DestroyPieceEvent，再 Disable + DestroySelf，最后 SetPiece(null) 清手）。
    // **必须用游戏自己的**：联机同步天然正确 —— RpcClearPiece 全员执行，房客按同样有效、不需要房主权限，
    //   不会出现"只有我丢掉了、别人手上还留着"。自己拼 SetPiece(null)+Destroy 就会漏掉别人那边。
    // 放弃之后手上是空的（就是游戏原生的"取消"语义），要还得从背包重选。
    public class DropHeldPiece : ITweak {
        private static ConfigEntry<KeyCode> _key;

        public void Initialize(IFeatureHost plugin) {
            try {
                SR.LocKey("Level", "Drop Held Piece", "放弃道具", "Drop held piece");
                SR.LocDesc("Level", "Drop Held Piece",
                    "排队模式里手上拿着道具又不想放了, 按这个键丢掉. 走游戏自己的通道, 全员都能看到, 之后正常进下一阶段.",
                    "In the queue phase, press this to discard the piece you are holding. Goes through the game's own channel; everyone sees it.");
                _key = plugin.Config.Bind("Level", "Drop Held Piece", KeyCode.None,
                    "按这个键就把手上拿着的那块丢掉（走游戏原生通道, 全员可见）, 填 None 就是不绑键.");
                SR.RegisterKey("关卡-放弃道具", _key, "press");
                SR.RegisterTick("放弃道具", Tick);
                // **R413-5**：新增的反射成员要登记，否则游戏改版后 SR.RefAudit 不会报出来
                // **Opt-16（审计 D#18）**：这两条登记原来指向 VersusControl.remainingPartyBoxes /
                //  PlacedThisRound，但**代码里从未反射这两个成员**（放弃道具靠的是下面那条
                //  `MsgPiecePlaced{PieceID=0}` 上行消息，不是直接改计数）-> 留着只会在游戏改名时
                //  报出误导性的"放弃道具失效"。删除；将来若真的改用它们，再连调用一起补登记。
            } catch (Exception __ex) { SR.Guard.Log("放弃道具 初始化", __ex); }
        }

        private static void Tick() {
            if (_key == null || _key.Value == KeyCode.None) return;
            if (!SR.ComboKeyDown(_key)) return;
            Perform();
        }

        // 真正干活（原 Tick 里"按键判定之后"那段，逻辑一字未改），拆出来是因为
        // 键位判定与执行分开写更清楚；门控只留在这一个地方。
        public static void Perform() {
            try {
                if (!SR.GateMaster) return;
                PiecePlacementCursor c = FindCursor();
                if (c == null) { SR.Notify(SR.T("未找到放置光标", "Placement cursor not found")); return; }
                if (c.Piece == null) { SR.Notify(SR.T("手上没有方块", "No piece in hand")); return; }
                if (!CallNativeRemoveUnplaced()) {
                    SR.Notify(SR.T("放弃失败", "Failed to drop the piece"));
                    SR.LogWarn("[放弃道具] 原生放弃接口不可用");
                    return;
                }
                // **R418**：只提示"已放弃"，不显示道具名（用户要求）
                SR.Notify(SR.T("已放弃当前方块", "Piece dropped"));
            } catch (Exception __ex) { SR.Guard.Log("放弃道具", __ex); }
        }

        // **本机的放置光标**。CursorInstance 是 LobbyPlayer 的属性（反编译 40959），
        //  不是 Player 的, Player 上只有 AssociatedLobbyPlayer。
        //  所以走 LobbyManager.lobbySlots（NetworkLobbyPlayer[]）筛 IsLocalPlayer。
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
                //退路：场上任一 PiecePlacementCursor（单机 / lobbySlots 还没生成时）
                PiecePlacementCursor[] all = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (all != null && all.Length > 0) return all[0];
            } catch (Exception __ex) { SR.Guard.Log("找放置光标", __ex); }
            return null;
        }

        // ================================================================
        // **R416-2（用户问：放弃道具别人能看到吗 / 修房客按了没反应）**
        //
        // 上一轮（R413）我改调 `VersusControl.CallRpcRemoveUnplacedObjects()`，**方向错了**。
        // 反编译 @147543：
        //   public void CallRpcRemoveUnplacedObjects() {
        //       if (!NetworkServer.active) { Debug.LogError("RPC ... called on client."); return; }
        //       ... SendRPCInternal(networkWriter, 0, "RpcRemoveUnplacedObjects");
        //   }
        // 它是**服务器下行广播**（SendRPCInternal，没有 SendCommandInternal、没有 Cmd 前缀），
        // 房客调它在第一行就被拦掉并打 LogError —— **房客按放弃等于什么都没发生，房主也看不到**。
        //
        // 玩家主动"丢弃自己手上的方块"在游戏里本来就有正规通道，就是 `ClearCurrentPiece` @73022：
        //   if (hasAuthority) { CallCmdClearPiece(); AkSound...; }
        // 而 `CallCmdClearPiece()` @73207 是 **public**（NetworkBehaviour 生成的 `[Command]` 包装），
        // 房客可以直接调 -> SendCommandInternal 上行 -> 服务端 `CmdClearPiece` @72991
        //   -> `CallRpcClearPiece()` -> `[ClientRpc] RpcClearPiece` @72997 全员执行：
        //        Piece.Disable() + Piece.DestroySelf(true, false, false)
        //        + DestroyPieceEvent（仅当已放置）+ SetPiece(null)
        // ⇒ **房客按放弃，全场（含房主）都能看到**。这才是"走原生通道"的正确入口。
        //
        // 之前 R392 那版直接反射调 private `ClearCurrentPiece()` 会跳创意模式，
        // 根因是它**绕过了 `OnInventory` @72356 里的 PARTY 门禁**（`if (GameMode == PARTY) return;`）——
        // 但那条门禁本来是"**不要自动清**"，玩家主动点放弃时游戏自己也是调 ClearCurrentPiece 的
        // （走 `CmdClearPiece`），所以现在走 Command 上行链，语义与原生完全一致。
        //
        // 房主/单机：`ClearCurrentPiece` 只在 hasAuthority 时才发命令，所以本地直接调它即可。
        // ================================================================
        private static bool CallNativeRemoveUnplaced() {
            try {
                PiecePlacementCursor c = FindCursor();
                if (c == null) return false;
                if (c.Piece == null) return false;
                int myNum = c.networkNumber;

                //--------------------------------------------------------------
                // **R417-1（用户报：放弃后进入创意模式道具库那种状态）**
                //
                // 上一轮我改调`CallCmdClearPiece`（public @73207，[Command] 上行，全员可见）——
                // 方向对（联机正确、房客也能用），但**只做了一半**。
                // `RpcClearPiece` @72997 只做三件事：DestroyPieceEvent（仅已放置）、
                // `Piece.Disable()`、`DestroySelf(...)`、`SetPiece(null)`。
                // 而原生派对模式的清件通道 `RpcRemoveUnplacedObjects` @146913 还额外做**三件**事：
                //   @146931  `RemainingPlacements[item.networkNumber - 1] = 0;`
                //   @146944  发 `MsgPiecePlaced{ PieceID = 0, ... }`
                //   ↓ 服务端 @146620 收到 -> 发 `PlacementSkippedEvent` @146623 -> `RemainingPlacements=0`
                //              并对**该玩家的光标**调 `Cursor.Hide()` @146634
                //
                // 缺了它们的后果（这就是用户看到的现象）：
                //  · `playersLeftToPlace` @14457 只要 `RemainingPlacements[i] > 0` 就为 true，
                //    而它在排队模式每轮被 `SetupPartyBoxForRound` @144798 重置为 1 ——
                //    不清零 => `DoPlaceMode` @145299 的 `if (!playersLeftToPlace)` 永远进不去
                //    => **永远不开下一个派对盒、永远不ChangeToPlayPhaseDelayed**（整局卡在放置阶段）；
                //  · 光标不会被 `Cursor.Hide()` 关掉：它仍然 Enabled、停在原地可移动，
                //    而 `SetPiece(null)` @71838 又把 `SelectionCollider.enabled` 置回 true
                //    —— **看起来就像"进入了某个放置/选择方块的 UI 状态"**。
                //
                // 顺带纠正我 R413 的错误诊断：`OnInventory` @72356 的 `GameMode == PARTY` 早退
                // 是**阻止**进道具库的（PARTY 下通往 `InventoryBookMenu` 的路对
                // `PiecePlacementCursor` 完全封闭），跳库不可能是绕过它造成的。
                // 用户描述的"创意模式道具库"实际就是上面那个**卡住的光标**。
                //
                // **改法**：走 `CallCmdClearPiece` 之后，照抄 @146939-146946 那段
                // 发 `MsgPiecePlaced{ PlayerNumber = myNum, PieceID = 0, ... }` ——
                // 这一条上行消息会触发完整的服务端收尾链（PlacementSkippedEvent
                // -> RemainingPlacements=0 -> Cursor.Hide()），**完全复刻原生**。
                //--------------------------------------------------------------
                // **Opt-50（T1 去反射 + 删死退路）**：CallCmdClearPiece 是 public 无参方法（反编译 73207）-> 直接调。
                //  于是"调不到就退到 private ClearCurrentPiece"那条退路**不可达**，一并删除（少一个反射登记）。
                bool sent = false;
                try { c.CallCmdClearPiece(); sent = true; }
                catch (Exception __ce) { SR.Guard.Log("放弃道具(Command 上行)", __ce); return false; }

                //**关键补一步**：上行 `MsgPiecePlaced{PieceID=0}`，让服务端把
                // `RemainingPlacements[myNum-1]` 清零并 `Cursor.Hide()`（照抄 @146939-146946）。
                // 没有它，放弃之后会卡在放置阶段出不来（详见上面的说明）。
                try {
                    MsgPiecePlaced skip = new MsgPiecePlaced();
                    skip.PlayerNumber = myNum;
                    skip.PiecePosition = Vector3.zero;
                    skip.PieceScale = Vector3.zero;
                    skip.PieceRotation = Quaternion.identity;
                    skip.PieceID = 0;   // **<=0** 让客户端 @72892 发 PlacementSkippedEvent、服务端 @146687 忽略放置
                    LobbyManager.instance.client.Send(NetMsgTypes.PiecePlaced, skip);
                } catch (Exception __se) { SR.Guard.Log("放弃道具(上报跳过)", __se); return sent; }
                return true;
            } catch (Exception __ex) { SR.Guard.Log("放弃道具（原生通道）", __ex); return false; }
        }
    }

// 派对盒三个动作（重抽 / 开箱 / 关闭）的实现。页面由 Level.RenderPartyBox 画。
public class LevelPartyBox : ITweak {
        private static ConfigEntry<KeyCode> _refreshPartyBoxKey;
        private static ConfigEntry<KeyCode> _refreshPiecesKey;
        private static ConfigEntry<KeyCode> _closePartyBoxKey;

        public static ConfigEntry<KeyCode> RefreshPartyBoxKeyEntry { get { return _refreshPartyBoxKey; } }
        public static ConfigEntry<KeyCode> RefreshPiecesKeyEntry { get { return _refreshPiecesKey; } }
        public static ConfigEntry<KeyCode> ClosePartyBoxKeyEntry { get { return _closePartyBoxKey; } }

        public void Initialize(IFeatureHost plugin) {
            try {
                _refreshPartyBoxKey = plugin.Config.Bind("Level", "Refresh Party Box Key", KeyCode.None,
                    "刷新派对盒完整流程的快捷键. 在关卡页的刷新派对盒按钮上右键就能绑, 再右键一次删掉. 按下就直接执行.");
                _refreshPiecesKey = plugin.Config.Bind("Level", "Refresh Pieces Key", KeyCode.None,
                    "刷新派对盒道具（只重抽、不开箱、不切相位）快捷键（按下即触发）");
                _closePartyBoxKey = plugin.Config.Bind("Level", "Close Party Box Key", KeyCode.None,
                    "关闭派对盒快捷键（结束派对盒选择、进入游玩阶段；按下即触发）");
                SR.RegisterKey("关卡-刷新派对盒", _refreshPartyBoxKey, "press");
                SR.RegisterKey("关卡-刷新道具", _refreshPiecesKey, "press");
                SR.RegisterKey("关卡-关闭派对盒", _closePartyBoxKey, "press");
                SR.RegisterTick("派对盒快捷键", Tick);
                //反射登记（原来登记在 EX，跟着功能一起搬过来；顺手去掉重复登记）
                SR.RefOverride("PartyBox.pieces", "关卡派对盒道具（清场/重抽）");
                // **Opt-18（审计 D#16）**：PartyBox.cursors 在下面 ClosePartyPickCursors 里走的是
                //  SR.RefField 读取，但从来没登记 -> 启动自检里看不到它（游戏改名后只有一条运行期告警）。
                // **Opt-18（审计 D#16）**：UIGraphic.Visible 也是裸反射（属性 + 兜底字段），一并登记。
                SR.RefOverride("VersusControl.showPartyBox", "关卡派对盒：完整流程");
                SR.RefOverride("VersusControl.IsSecondBox", "关卡派对盒：第二次开箱");
                SR.RefOverride("VersusControl.ToPlaceMode", "关卡派对盒：房客一起进建造状态");
                SR.RefOverride("VersusControl.SetupPlacementCursors", "关卡派对盒：同步放置光标");
                SR.RefOverride("GameControl.nextPhase", "关卡派对盒：结束放置阶段");
            } catch (Exception __ex) { SR.Guard.Log("关卡派对盒 初始化", __ex); }
        }

        //快捷键轮询：与页面按钮共用同一批动作
        private static void Tick() {
            try {
                if (!SR.GateMaster) return;
                if (SR.ComboKeyDown(_refreshPartyBoxKey)) RefreshPartyBox();
                if (SR.ComboKeyDown(_refreshPiecesKey)) RefreshPartyBoxPieces();
                if (SR.ComboKeyDown(_closePartyBoxKey)) ClosePartyBox();
            } catch (Exception __ex) { SR.Guard.Log("派对盒快捷键", __ex); }
        }

        //清空当前派对盒里的可选道具。
        // 刷新派对盒道具：**完整重走一遍游戏自己的派对盒流程**。
        // 游戏自己的入口（反编译确认）：showPartyBox() = SetupPartyBoxForRound(true) + ShowBox(IsSecondBox)
        //   · SetupPartyBoxForRound(true)：按本轮设置重算 blockSelectionMode/Density/Difficulty，
        //     再用 ChoosePieces(...) 按游戏自己的权重抽道具，并把这一轮的派对盒阶段重置回起点；
        //   · ShowBox(IsSecondBox)：开箱动画 + 可见 + 全员可再选一次，只在 HasAuthority 为真时自己广播 MsgPartyBoxOpen；
        //   · CallRpcShowPartyBox()：ClientRpc，各客户端也执行 showPartyBox()（客户端 hasAuthority=false，
        //     只做本地重置 + ShowBox，不会重复生成道具）。
        // 所以房主端只要三步：广播销毁旧道具 -> showPartyBox() -> CallRpcShowPartyBox()，全员就一致了。
        public static void RefreshPartyBox() {
            try {
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) { SR.Notify(SR.T("请先进入派对模式对局", "Not in a party match")); return; }
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SR.Notify(SR.T("未找到派对盒", "Party box not found")); return; }
                // 权限判定走 SR 统一门面（Cap.HostOnly）：道具由房主端生成、需服务器广播 ->
                //  否则"房客点了没反应还不说原因"。提示文案也由 SR 统一给，不再各处自写。
                if (!SR.CanDoHost()) return;
                int oldPieces = DestroyPartyBoxPieces(pb);   //旧道具必须广播销毁：ChoosePieces 内部的 ClearPieces 只用 Object.Destroy
                System.Reflection.MethodInfo show = SR.RefMethod(typeof(VersusControl), "showPartyBox", "关卡派对盒流程");
                if (show != null) {
                    show.Invoke(vc, null);              //游戏自己的完整流程（重抽 + 开箱）
                    try { vc.CallRpcShowPartyBox(); } catch (System.Exception __ex) { SR.LogWarn("重新开箱(通知客户端)失败: " + __ex.Message); }
                    SR.Notify(SR.T("已重抽道具并重新开箱", "Rerolled the items and reopened the box"));
                } else {
                    FallbackRefresh(pb, oldPieces);     //兜底（游戏改名/版本不同）：手工走同样的两步
                }
                //不在建造状态时打开派对盒：道具能选，但游戏 Phase 还是 PLAY -> 选完放不下。
                //把游戏的 nextPhase 顶到 PLACE，让它自己走 ToPlaceMode()（含 StartPhaseEvent + RPC，全员同步）。
                ForcePlacePhase(vc);
            } catch (System.Exception e) { SR.LogWarn("刷新派对盒失败: " + e.Message); }
        }

        //强制进入放置阶段：只改 nextPhase，游戏 Update 的相位状态机随后自己调 ToPlaceMode()
        //（直接反射调 ToPlaceMode 也能进，但那样绕过了游戏自己的等待/同步流程）。
        //让**全员**进入建造/放置状态。
        //原来只改房主本地的 nextPhase（房客不会跟着进建造状态）-> 现在优先调用游戏**原生入口**：
        //   VersusControl.ToPlaceMode()  , 游戏自己进放置阶段用的方法（内部含 StartPhaseEvent 与
        //    SetupPlacementCursors / RpcSetupPlacementCursors 等同步），因此房客会一起进入建造状态。
        //若 ToPlaceMode 不可用（版本差异）-> 回退：房主 nextPhase=PLACE，并显式 CallRpcSetupPlacementCursors 让房客同步。
        private static void ForcePlacePhase(VersusControl vc) {
            try {
                if (vc == null) return;
                System.Reflection.MethodInfo toPlace = SR.RefMethod(typeof(VersusControl), "ToPlaceMode", "关卡派对盒：房客一起进建造状态");
                if (toPlace != null) {
                    try { toPlace.Invoke(vc, null); return; } catch (System.Exception __t) { SR.LogWarn("ToPlaceMode 失败，回退: " + __t.Message); }
                }
                System.Reflection.FieldInfo np = SR.RefField(typeof(GameControl), "nextPhase", "关卡派对盒：结束放置阶段");
                if (np != null) {
                    object cur = np.GetValue(vc);
                    if (cur == null || cur.ToString() != "PLACE") np.SetValue(vc, System.Enum.Parse(np.FieldType, "PLACE"));
                }
                //房客同步：让各客户端也建立放置光标（进入建造状态的关键一步）
                try {
                    System.Reflection.MethodInfo su = SR.RefMethod(typeof(VersusControl), "SetupPlacementCursors", "关卡派对盒：同步放置光标");
                    if (su != null) su.Invoke(vc, null);
                    // **Opt-50（T1）**：CallRpcSetupPlacementCursors 是 public 方法（反编译 147378：`CallRpcSetupPlacementCursors(bool waitForForcedPieces)`）-> 直接调；
                    //  原来"按形参真实类型造默认值"那一整段（bool -> Activator.CreateInstance）随之删除。
                    vc.CallRpcSetupPlacementCursors(false);
                } catch (System.Exception __s) { SR.LogWarn("同步放置光标失败: " + __s.Message); }
            } catch (System.Exception e) { SR.LogWarn("切换到放置阶段失败: " + e.Message); }
        }

        //刷新派对盒道具：只重抽盒子里的道具，不重新开箱、不动相位（"单纯刷新道具"）。
        internal static void RefreshPartyBoxPieces() {
            try {
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) { SR.Notify(SR.T("请先进入派对模式对局", "Not in a party match")); return; }
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SR.Notify(SR.T("未找到派对盒", "Party box not found")); return; }
                if (!SR.CanDoHost()) return;   //重建道具同样要服务器权限（见 SR.Cap.HostOnly）
                int oldPieces = DestroyPartyBoxPieces(pb);
                int amount = oldPieces > 0 ? oldPieces : 1;
                // **Opt-50（T1）**：ChoosePieces 是 public 方法（反编译 87256：`ChoosePieces(int amount, List<Placeable> forceBlocks)`）
                pb.ChoosePieces(amount, null);
                SR.Notify(SR.T("已刷新派对盒道具（", "Rerolled the party box items (") + amount + SR.T(" 件）", " items)"));
            } catch (System.Exception e) { SR.LogWarn("刷新派对盒道具失败: " + e.Message); }
        }

        //关闭派对盒：**完全走游戏原生流程**, 只把 nextPhase 顶到 PLAY，剩下的由游戏自己完成
        //（ChangeToPlayPhaseDelayed -> 收掉放置光标 -> 关闭派对盒；全员同步由游戏的状态机与 RPC 负责）。
        // 不再手工 Hide()、不再手工复位 PickCursor、不再自己发 MsgPartyBoxOpen 原始消息,
        //   那些做法会**打断别人正在进行的放置状态**（正是要避免的）。
        // **R417-2**：关掉派对盒里那些 `PartyPickCursor`（= 用户看到的"残留光标"）。
        // 原生 `PartyBox.Hide()` @87701 压根不禁用它们（只有玩家离开 @87906 才关），
        // 而 `openFlaps` @87754 把它们 Enable 了 —— 强行关盒就必须补这一步。
        // 用游戏自己的 `Cursor.Disable()`（与 @87906 同一条路），不手工SetActive。

        // **R416-3**：本机的放置光标（CursorInstance 在 LobbyPlayer 上，不在 Player 上）

        // **R416-3**：全场有没有人真的拿到了方块（Piece 非 null）。
        // 这是"能不能关盒子进放置阶段"的唯一正确判据 —— 游戏自己判断放置阶段能否开始
        // 用的也是 `AreAllPiecesReady()`（遍历 PlacedThisRound 要求每个都 Placed），
        // 与"有没有人拿着方块"是同一件事的两面。

        // 关闭派对盒：**完全走游戏原生机制**（用户要求：不做任何本地处理）
        //
        // 原生链路（反编译逐条核对，全在 VersusControl / PartyBox 自己的代码里）：
        //   ① 放置阶段的 Update 每帧 `placementTimer -= Time.unscaledDeltaTime;`（145350）
        //   ② 计时进入警告窗口 -> `showingPlacementWarning = true`（145352）
        //   ③ 计时到 0 -> 房主执行 `CallRpcSendPlacementTimerDone()`（145373，游戏自己的日志就写着
        //      "Sending RPC to close party box"）-> 每个客户端 `RpcSendPlacementTimerDone()`（146907）
        //      -> `PlacementTimerDoneEvent`
        //   ④ 相位推进 -> `ChangeToPlayPhaseDelayed()`（145378）：`EndPhaseEvent(PLACE)` + `nextPhase = PLAY`
        //   ⑤ 相位机切换 -> `CallRpcStartPhase(PLAY)`（127479）-> 每个客户端 `RpcStartPhase`（126877）
        //      -> `StartPhaseEvent(PLAY)`
        //   ⑥ 每个客户端的 `PartyBox.handleEvent`（87824）收到 `Phase == PLAY` -> `ToPlayMode()`
        //      -> 盒子关闭 + `closeFlaps()`
        //   ⑦ `VersusControl.handleEvent(PartyBoxEvent(false))` -> `SetupPlacementCursors()`
        //      —— 已经拿到道具的人在这里拿到放置光标，正常进入放置阶段
        //
        // 所以模组**只需要把放置计时推到结束**：剩下的（广播给全员、关盒、收/换光标、相位同步）
        // 全部由游戏自己的状态机与 RPC 完成。**不再**本地 `Hide()`、**不再**本地收 `PartyPickCursor`、
        // **不再**自己发 `MsgPartyBoxOpen` 广播 —— 那些都是本地处理，别人那边要靠我们自己补，容易漏。
        //
        // 唯一前置（游戏原生语义，刻意保留）：`AreAllPiecesReady()`（145323）—— 还有人没选完时
        // 游戏自己不会推进相位；此时用「放弃道具」让他放掉即可。
        internal static void ClosePartyBox() {
            try {
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) { SR.Notify(SR.T("请先进入派对模式对局", "Not in a party match")); return; }
                // 相位推进是服务器权限（原生 RPC 也只在房主侧发），权限门控与同分区其它按钮一致
                if (!SR.CanDoHost()) return;
                // placementTimer / showingPlacementWarning 都是 VersusControl 的 protected 字段
                //（反编译 143899 / 1439xx），走项目统一的反射登记与缓存机制。
                System.Reflection.FieldInfo pt = SR.RefField(typeof(VersusControl), "placementTimer",
                    "关卡派对盒：关闭（把原生放置计时推到结束）");
                System.Reflection.FieldInfo pw = SR.RefField(typeof(VersusControl), "showingPlacementWarning",
                    "关卡派对盒：关闭（进入原生的放置结束提示窗口）");
                if (pt == null || pw == null) {
                    SR.Notify(SR.T("关闭派对盒失败（游戏成员不可用）", "Failed to close the party box (member unavailable)"));
                    return;
                }
                // 计时归零 + 进入警告窗口：下一帧游戏自己就会走 ③④⑤⑥⑦ 的完整原生流程
                pt.SetValue(vc, 0f);
                pw.SetValue(vc, true);
                SR.LogInfo("[关卡派对盒] 已把原生放置计时推到结束，后续交给游戏自己的流程（关盒/同步/光标）");
                SR.Notify(SR.T("已关闭派对盒", "Party box closed"));
            } catch (Exception __ex) { SR.LogWarn("关闭派对盒失败: " + __ex.Message); }
        }

        //旧道具广播销毁（房主端）：UnityEngine.Object.Destroy 不通知客户端，客户端会残留旧道具
        //返回销毁前的件数：兜底路径要靠它"按件数重抽"（此处已 list.Clear()，之后再也读不到）
        private static int DestroyPartyBoxPieces(PartyBox pb) {
            int count = 0;
            try {
                System.Reflection.FieldInfo f = SR.RefField(typeof(PartyBox), "pieces", "关卡派对盒道具（清场/重抽）");
                System.Collections.IList list = f != null ? f.GetValue(pb) as System.Collections.IList : null;
                if (list == null) return 0;
                List<UnityEngine.GameObject> dead = new List<UnityEngine.GameObject>();
                foreach (object o in list) {
                    UnityEngine.Component c = o as UnityEngine.Component;
                    if (c != null && c.gameObject != null) dead.Add(c.gameObject);
                }
                for (int i = 0; i < dead.Count; i++) {
                    try { UnityEngine.Networking.NetworkServer.Destroy(dead[i]); }
                    catch (Exception __ex) { SR.LogWarn("刷新派对盒(销毁旧道具)失败: " + __ex.Message); }
                }
                list.Clear();
                count = dead.Count;
            } catch (Exception e) { SR.LogWarn("刷新派对盒(清理)失败: " + e.Message); }
            return count;
        }

        //兜底：找不到 VersusControl.showPartyBox 时，自己按同样的两步来（按件数重抽 + 开箱广播）
        private static void FallbackRefresh(PartyBox pb, int amount) {
            try {
                if (amount <= 0) amount = 1;   //件数由调用方在销毁前传入（这里 pieces 已被清空）
                pb.ChoosePieces(amount, null);   // **Opt-50（T1）**：public 方法（87256）直接调，取消反射
                ReopenPartyBox(pb);
                SR.Notify(SR.T("已重抽派对盒道具", "Rerolled the party box items"));
            } catch (System.Exception e) { SR.LogWarn("刷新派对盒(兜底)失败: " + e.Message); }
        }

        //兜底用的开箱：PartyBox.ShowBox + 补广播 MsgPartyBoxOpen（客户端 handleEvent -> OnBoxOpen -> ShowBox）
        private static void ReopenPartyBox(PartyBox pb) {
            try {
                if (pb == null) return;
                bool extra = false;
                try {
                    VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                    System.Reflection.FieldInfo f = vc != null ? SR.RefField(typeof(VersusControl), "IsSecondBox", "关卡派对盒流程") : null;
                    if (f != null) extra = (bool)f.GetValue(vc);
                } catch { }
                try { pb.ShowBox(extra); } catch (Exception __ex) { SR.LogWarn("重新开箱(本地)失败: " + __ex.Message); }
                try {
                    if (UnityEngine.Networking.NetworkServer.active) {
                        MsgPartyBoxOpen msg = new MsgPartyBoxOpen();
                        msg.IsOpen = true;
                        msg.isExtraBox = extra;
                        UnityEngine.Networking.NetworkServer.SendToAll(NetMsgTypes.PartyBoxOpen, msg);
                    }
                } catch (Exception __ex) { SR.LogWarn("重新开箱(广播)失败: " + __ex.Message); }
            } catch (System.Exception e) { SR.LogWarn("重新开箱失败: " + e.Message); }
        }
    }
}