using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    //派对盒子炸弹：房主端统计对局内所有在线成员的「炸弹！」快捷短语
    //（EmoteMeanings.EMOTE_Bomb，按枚举码识别，与多语言/文本无关），全员都发过才往当前
    //派对盒子塞入一个可选取的炸弹；型号可配置（PartyBox.BombPrefab 的第几个）。
    //⚠ 实验功能：动态改 PartyBox 私有 pieces + 网络同步，需游戏内验证。
    public class PartyBomb : ITweak {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _bombSize;
        public static bool Enabled = false;

        //已发送过「炸弹！」快捷消息的成员（networkNumber 去重）
        private static readonly HashSet<int> _bombSent = new HashSet<int>();

        public void Initialize(MainPlugin plugin) {
            _enabled = plugin.Config.Bind("Level", "Party Bomb", false,
                "派对盒子炸弹：房主开启后，对局内所有成员都发过「炸弹！」快捷短语时才往派对盒子塞炸弹。仅房主生效。");
            _bombSize = plugin.Config.Bind("Level", "Party Bomb Type", 0,
                "生成的炸弹型号（PartyBox.BombPrefab 的第几个：0 起始；不同下标对应该盒子里不同大小的炸弹）。首次生成会打日志列出可选型号。");
            Enabled = _enabled.Value;
            _enabled.SettingChanged += (s, e) => Enabled = _enabled.Value;
            Harmony.CreateAndPatchAll(typeof(PartyBomb));
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
                    MainPlugin.ModLogger.LogInfo("[PartyBomb] 已收到 " + _bombSent.Count + "/" + online.Count + " 名成员的「炸弹！」快捷消息");
                    return;
                }
                _bombSent.Clear(); //触发一次后清空，等待下一轮全员再发
                MainPlugin.ModLogger.LogInfo("[PartyBomb] 全员已发「炸弹！」，生成炸弹（型号 #" + BombSize + "）");
                AddBombToPartyBox();
            } catch (Exception e) {
                MainPlugin.ModLogger.LogWarning("[PartyBomb] 检测失败: " + e.Message);
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
                if (pb == null) { MainPlugin.ModLogger.LogWarning("[PartyBomb] 未找到派对盒子"); return; }
                if (pb.BombPrefab == null || pb.BombPrefab.Length == 0) { MainPlugin.ModLogger.LogWarning("[PartyBomb] 派对盒子无炸弹预制体"); return; }
                Placeable bombPlaceable = PickBombPlaceable(pb, BombSize);
                if (bombPlaceable == null || bombPlaceable.PickableBlock == null) { MainPlugin.ModLogger.LogWarning("[PartyBomb] 炸弹档 " + BombSize + " 无 PickableBlock"); return; }

                //pieces 是 private：反射获取后加入
                var f = HarmonyLib.AccessTools.Field(typeof(PartyBox), "pieces");
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
                piece.FindPartyBox = true; //让 piece 找到并跟随派对盒子（同游戏自身 spawn）
                try { piece.setInitialScale(GameSettings.GetInstance().partyBoxItemScale); } catch (Exception __ex) { SR.Guard.Log("设置炸弹初始缩放", __ex); }
                piece.InPartybox = true;
                piece.Enable();
                NetworkServer.Spawn(piece.gameObject);

                MainPlugin.ModLogger.LogInfo("[PartyBomb] 已向派对盒子塞入炸弹 (" + BombName(BombSize) + ", " + piece.name + ")");
            } catch (Exception e) {
                MainPlugin.ModLogger.LogWarning("[PartyBomb] 加炸弹失败: " + e.Message);
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
            int idx = Mathf.Clamp(size, 0, arr.Length - 1);
            return arr[idx];
        }
    }
}
