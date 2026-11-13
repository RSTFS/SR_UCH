// 关卡 · 派对炸弹（PartyBomb）与放弃道具（DropHeldPiece）。
// 从 Level.PartyBox.cs 拆出来：那个文件原来同时装着 4 个功能类。
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
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
                SRApi.LocKey("Level", "Party Bomb", "派对盒子炸弹", "Party bomb");
                SRApi.LocKey("Level", "Party Bomb Type", "炸弹类型", "Party bomb type");
                SRApi.LocDesc("Level", "Drop Held Piece", "放弃道具按键\n派对阶段丢弃手上的道具（游戏原生通道）\n请勿删除边界墙和出生方块，删了无法复原", "Drop held piece\ndiscards the held piece during the pick phase via the game channel\nnever delete boundary walls or spawn blocks - it cannot be undone");
                _key = plugin.Config.Bind("Level", "Drop Held Piece", KeyCode.None,
                    "按这个键就把手上拿着的那块丢掉（走游戏原生通道, 全员可见）, 填 None 就是不绑键.");
                SRApi.RegisterKey("关卡-放弃道具", _key, "press");
                SRApi.RegisterTick("KEY_DROP", Tick);
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具 初始化", __ex); }
        }

        private static void Tick() {
                // 不做帧取模节流：一次点按可能只有 50-100ms，取模会整段漏掉（"不跟手"）。
                // ComboKeyDown 内部已按帧缓存，每帧调用开销可忽略。
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


}
