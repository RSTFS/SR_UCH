// 关卡 · 派对炸弹（PartyBomb）与放弃道具（DropHeldPiece）。
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

        // 已发过炸弹！消息的成员（networkNumber 去重）。
        private static readonly HashSet<int> _bombSent = new HashSet<int>();


        public void Initialize(IFeatureHost plugin) {
            SRApi.RegisterDismissHook(delegate { _bombSent.Clear(); });
            // 反射的私有成员须在此登记，游戏改名时启动自检会告警。
            SRApi.Ref.Override("PartyBox.pieces", "派对盒炸弹（生成/清理）");
            _enabled = plugin.Config.Bind("Level", "Party Bomb", false,
                "派对盒子炸弹: 房主开启后, 对局里所有人都发过炸弹这个快捷短语, 才会往派对盒子里塞炸弹. 只有房主生效.");
            _bombSize = plugin.Config.Bind("Level", "Party Bomb Type", 0,
                "塞进去的是哪种炸弹, 填 PartyBox.BombPrefab 里的第几个, 从 0 开始. 不同下标对应盒子里不同大小的炸弹. 第一次生成的时候日志里会列出有哪些可选.");
            Enabled = _enabled.Value;
            _enabled.SettingChanged += (s, e) => Enabled = _enabled.Value;
            // 带 try/catch：单个补丁目标失效只丢本类功能。
            try { Harmony.CreateAndPatchAll(typeof(PartyBomb)); }
            catch (Exception e) { SRApi.LogError("派对盒炸弹 补丁注册失败: " + e.Message); }
            // networkNumber 会跨对局复用：换场景与对局开始两处都要清理，否则同号玩家被误判。
            SRApi.RegisterSceneHook(delegate { _bombSent.Clear(); });
        }

        // 快捷短语走 ChatDisplay.DisplayNewMessage；房主端收集每位成员的 EMOTE_Bomb。
        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage", new Type[] { typeof(ChatMessageDetails) })]
        [HarmonyPrefix]
        static void OnBombEmote(ChatMessageDetails chatMessageDetails) {
            if (!SRApi.GateMaster || !Enabled) return;
            try {
                if (chatMessageDetails.EmoteType != EmoteMeanings.EMOTE_Bomb) return;
                // 仅服务器端统计与生成：炸弹需 NetworkServer.Spawn 广播全员。
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

        // 往当前派对盒塞入一个炸弹；仅在服务端调用。
        static void AddBombToPartyBox() {
            try {
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SRApi.LogWarn("[PartyBomb] 未找到派对盒子"); return; }
                if (pb.BombPrefab == null || pb.BombPrefab.Length == 0) { SRApi.LogWarn("[PartyBomb] 派对盒子无炸弹预制体"); return; }
                Placeable bombPlaceable = PickBombPlaceable(pb, BombSize);
                if (bombPlaceable == null || bombPlaceable.PickableBlock == null) { SRApi.LogWarn("[PartyBomb] 炸弹档 " + BombSize + " 无 PickableBlock"); return; }

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
                piece.NetworkStartPosition = piece.transform.localPosition; // Start() 会用 StartPosition 覆盖
                piece.FindPartyBox = true; // 让 piece 跟随派对盒（同游戏自身 spawn）
                try { piece.setInitialScale(GameSettings.GetInstance().partyBoxItemScale); } catch (Exception __ex) { SRApi.Guard.Log("设置炸弹初始缩放", __ex); }
                piece.InPartybox = true;
                piece.Enable();
                NetworkServer.Spawn(piece.gameObject);

                SRApi.LogInfo("[PartyBomb] 已向派对盒子塞入炸弹 (" + BombName(BombSize) + ", " + piece.name + ")");
            } catch (Exception e) {
                SRApi.LogWarn("[PartyBomb] 加炸弹失败: " + e.Message);
            }
        }

        static string BombName(int size) {
            switch (size) {
                case 0: return "小炸弹";
                case 2: return "超级炸弹";
                default: return "大炸弹";
            }
        }

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
            SRApi.LogWarn("[派对盒炸弹] 按名字未匹配到型号（size=" + size + "），已退回按索引取第 " + Mathf.Clamp(size, 0, arr.Length - 1) + " 个");
            int idx = Mathf.Clamp(size, 0, arr.Length - 1);
            return arr[idx];
        }
    }

    // 放弃道具：在派对阶段手上有道具时使用，必须走原生 Command 上行链；
    // 自行 SetPiece(null)+Destroy 会让别人手上仍留着方块。
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
                SRApi.RegisterKey("关卡-放弃道具", _key, "press", Perform);
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具 初始化", __ex); }
        }

        public static void Perform() {
            try {
                if (!SRApi.GateMaster) return;
                PiecePlacementCursor c = FindCursor();
                if (c == null) { SRApi.NotifyT("未找到放置光标", "Placement cursor not found"); return; }
                if (c.Piece == null) { SRApi.NotifyT("手上没有方块", "No piece in hand"); return; }
                if (!CallNativeRemoveUnplaced()) {
                    SRApi.NotifyT("放弃失败", "Failed to drop the piece");
                    SRApi.LogWarn("[放弃道具] 原生放弃接口不可用");
                    return;
                }
                SRApi.NotifyT("已放弃当前方块", "Piece dropped");
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具", __ex); }
        }

        // 本机放置光标：CursorInstance 挂在 LobbyPlayer 上，故经 lobbySlots 筛 IsLocalPlayer。
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
                PiecePlacementCursor[] all = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (all != null && all.Length > 0) return all[0];
            } catch (Exception __ex) { SRApi.Guard.Log("找放置光标", __ex); }
            return null;
        }

        // 须走原生 Command 上行链：房客调用全场可见；CallRpcRemoveUnplacedObjects 是服务器下行，房客会被拦。
        private static bool CallNativeRemoveUnplaced() {
            try {
                PiecePlacementCursor c = FindCursor();
                if (c == null) return false;
                if (c.Piece == null) return false;
                int myNum = c.networkNumber;

                // 还须补发 MsgPiecePlaced{PieceID=0}：服务端据此清零 RemainingPlacements 并发
                // PlacementSkippedEvent，否则光标残留、放置阶段永不结束。
                bool sent = false;
                try { c.CallCmdClearPiece(); sent = true; }
                catch (Exception __ce) { SRApi.Guard.Log("放弃道具(Command 上行)", __ce); return false; }

                try {
                    MsgPiecePlaced skip = new MsgPiecePlaced();
                    skip.PlayerNumber = myNum;
                    skip.PiecePosition = Vector3.zero;
                    skip.PieceScale = Vector3.zero;
                    skip.PieceRotation = Quaternion.identity;
                    skip.PieceID = 0; // <=0：客户端据此发 PlacementSkippedEvent
                    LobbyManager.instance.client.Send(NetMsgTypes.PiecePlaced, skip);
                } catch (Exception __se) { SRApi.Guard.Log("放弃道具(上报跳过)", __se); return sent; }
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("放弃道具（原生通道）", __ex); return false; }
        }
    }


}
