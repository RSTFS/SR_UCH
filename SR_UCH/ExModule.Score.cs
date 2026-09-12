using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using SR_UCH.Tweaks; //SR / Loc

namespace SR_UCH_EX {
public partial class ExModule {

// ==== 分区：Score（加分 / 加金币 / 达标立即结算）====


        //--- score: 加分。按 Score Type 下拉框的标准分值走游戏原生 PointAwarded
        //（房主原生转发 → 全员计分板显示分块），分值来自 GameSettings.PointTypeValue（如获胜 50、陷阱 10）。
        public static void AddScore() {
            try {
                int target = CurrentTarget();
                if (target < 0) return;
                int amount;
                PointBlock.pointBlockType type = _scoreType != null ? _scoreType.Value : PointBlock.pointBlockType.win;
                //游戏原生 PointAwarded → 全员加分显示（房主原生广播）
                bool sent = false;
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null && lm.client != null && lm.client.isConnected) {
                        lm.client.Send(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                            PlayerNumber = target, PointType = type, AlwaysAward = true
                        });
                        sent = true;
                    }
                } catch { }
                try { amount = GameSettings.GetInstance().PointTypeValue(type); } catch { amount = 1; }
                if (amount == 0) amount = 1;
                if (!sent) ApplyScore(target, amount);
                //注意：不再本地立即显示分块——PointAwarded 已把分块记入全员 tally，
                //结算时计分板统一显示一次；这里再显示会与结算重复（分块双份）。
                //提示带分数类型（原来只有数值）：加分+50获胜分：玩家名
                Notify("加分+" + amount + SR.EnumNameZh(type.ToString()) + "分：" + PlayerName(target));
                //服务器端（房主/单机/本地派对）：加分后检查是否达获胜分，达标则立即结算判胜
                MaybeEndRoundNow();
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("评分失败: " + e.Message);
            }
        }

        private static void ApplyScore(int number, int amount) {
            try {
                ScoreKeeper sk = ScoreKeeperInstance();
                if (sk == null) {
                    Notify("记分板不可用（仅派对/对战/创意对局内）");
                    return;
                }
                GamePlayer gp = null;
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null && lm.PlayerTracker != null) gp = lm.PlayerTracker.GetGamePlayer(number);
                } catch { }
                if (gp == null) {
                    Notify("目标不在计分板（需在对局内）");
                    return;
                }
                //房客本地执行：确保目标在 playerTotal（这样“给别的房客加分”在房客面板上也能生效）
                EnsureInPlayerTotal(sk, gp);
                sk.AddPointsDirectly(gp, amount);
                //派对计分板是“分块图标”制：直接加总分在面板上看不到，需显示一个分块图标
                PointBlock.pointBlockType bt = _scoreType != null ? _scoreType.Value : PointBlock.pointBlockType.win;
                ShowScoreboardAdd(number, bt);
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("加分失败: " + e.Message);
            }
        }

        //在派对/对战计分板上显示一个分块图标（计分板是分块图标制，直接加总分/金币在面板上看不到）
        private static void ShowScoreboardAdd(int target, PointBlock.pointBlockType type) {
            try {
                VersusControl vs = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vs == null) return;
                System.Reflection.FieldInfo f = HarmonyLib.AccessTools.Field(typeof(VersusControl), "graphScoreBoardInstance");
                if (f == null) return;
                GraphScoreBoard gsb = f.GetValue(vs) as GraphScoreBoard;
                if (gsb == null) return;
                gsb.displayNewScore(new List<PointBlock> { new PointBlock(type, target) });
                gsb.Show(2f);
            } catch { }
        }

        //--- coin: 加金币。按 amount 发送多个 PointAwarded(coin) 分块（全员计分板显示对应数量金币分块）
        //+ 本地金币计数累计。每个金币分块 = 结算时显示 1 个金币图标。
        public static void AddCoin() {
            try {
                int target = CurrentTarget();
                if (target < 0) return;
                int amount = _coinAmount != null ? Mathf.Max(1, _coinAmount.Value) : 1;
                //网络上限（P0-3）：每个金币 = 一条 PointAwarded。设 10000 会瞬间发 1 万条消息，
                //足以卡死甚至解散房间。对齐主模组补分的「总 300 条」上限。
                const int CoinSendCap = 300;
                if (amount > CoinSendCap) {
                    Notify("金币数量已限制为 " + CoinSendCap + "（设置值 " + amount + "）");
                    amount = CoinSendCap;
                }
                //全员可见：按 amount 循环发送多个金币分块 → 结算显示 amount 个金币（而不是只 1 个）
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null && lm.client != null && lm.client.isConnected) {
                        for (int i = 0; i < amount; i++) {
                            lm.client.Send(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                                PlayerNumber = target, PointType = PointBlock.pointBlockType.coin, AlwaysAward = true
                            });
                        }
                    }
                } catch { }
                //本地金币（计数/显示）：累加 amount
                GiveCoinLocally(target, amount);
                Notify("金币+" + amount + ": " + PlayerName(target));
                //服务器端：加金币后检查是否达获胜分，达标则立即结算判胜
                MaybeEndRoundNow();
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("加金币失败: " + e.Message);
            }
        }

        //服务器端（房主/单机/本地派对）加分/金币后：等分块消息落地一帧，再检查是否有玩家
        //总分达到获胜分 → 达标则调用原生结算流程（CallRpcShowScoreboard）立即判胜广播全员。
        //联机房客（房主未装 mod）无法触发（需要 NetworkServer.active / 服务器端权限）。
        private static void MaybeEndRoundNow() {
            try {
                if (!UnityEngine.Networking.NetworkServer.active) return; //仅服务器端
                if (LobbyManager.instance == null || LobbyManager.instance.CurrentGameController == null) return;
                _pendingEndRoundAt = Time.unscaledTime + 0.1f; //等分块 addPointBlock 落地
            } catch { }
        }

        //加分/金币后实时刷新得分板——**已移除**。原因：只在服务器端本地 tally 清空会导致
        //房客端（网络同步的 newPointBlocks 未清空）在结算时重复显示分块，造成跨端不一致。
        //改为：加分走 PointAwarded 网络消息，各端 newPointBlocks 一致，结算时 RpcShowScoreboard
        //统一显示一次 + tally。实时变化会破坏这种一致性，故不做。

        //每帧检查：达标则立即结算（Harmony 挂到 GameControl.Update）。
        //达标判断用「已 tally 总分 + newPointBlocks 待结算分」临时计算，不破坏分块（不 tally 清空），
        //保证各端 newPointBlocks 一致，结算时统一显示一次。
        private static void CheckPendingEndRound() {
            if (_pendingEndRoundAt < 0f) return;
            if (Time.unscaledTime < _pendingEndRoundAt) return;
            _pendingEndRoundAt = -1f;
            try {
                if (!UnityEngine.Networking.NetworkServer.active) return;
                VersusControl vs = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vs == null) return;
                ScoreKeeper sk = ScoreKeeperInstance();
                if (sk == null) return;
                List<PointBlock> blocks = null;
                try {
                    System.Reflection.FieldInfo f = HarmonyLib.AccessTools.Field(typeof(ScoreKeeper), "newPointBlocks");
                    if (f != null) blocks = f.GetValue(sk) as List<PointBlock>;
                } catch { }
                GameSettings gs = GameSettings.GetInstance();
                int maxScore = gs != null ? gs.MaxScore : 0;
                bool reached = false;
                foreach (int n in OnlineNumbers()) {
                    int pending = 0;
                    if (blocks != null) {
                        foreach (PointBlock b in blocks) {
                            if (b.playerNumber == n) {
                                try { pending += b.pointValue; } catch { }
                            }
                        }
                    }
                    if (GetScore(n) + pending >= maxScore) { reached = true; break; }
                }
                if (reached) {
                    vs.CallRpcShowScoreboard(2f, false, false); //原生结算流程：显示计分板+tally+判胜广播
                    Notify("已达获胜分，立即结算");
                }
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("立即结算失败: " + e.Message);
            }
        }

        //mod 独立金币显示计数（只统计 EX 加的金币，用于头顶显示）。
        //拾取放置的金币走游戏原版 CoinsCollected（增加但头顶不显示，金币背在身上）；
        //EX 加金币更新此计数并刷新头顶显示。这样头顶只显示 EX 加的金币数。
        private static readonly Dictionary<int, int> _coinCounts =
            new Dictionary<int, int>();

        //清理已离开玩家的金币显示计数（#14）：_coinCounts 以 networkNumber 为键且从不清理，
        //号码被复用时新玩家会继承上一位的头顶金币显示数。原来只在 SetupStart 整体清一次，
        //这里再做一次低频（2 秒）增量清理，覆盖“中途换人但没重开”的情况。
        private static float _coinPruneAt = -1f;
        private static void PruneCoinCounts() {
            if (Time.unscaledTime < _coinPruneAt) return;
            _coinPruneAt = Time.unscaledTime + 2f;
            try {
                if (_coinCounts.Count == 0) return;
                List<int> live = OnlineNumbers();
                List<int> dead = null;
                foreach (int k in _coinCounts.Keys) {
                    if (!live.Contains(k)) { if (dead == null) dead = new List<int>(); dead.Add(k); }
                }
                if (dead != null) { for (int i = 0; i < dead.Count; i++) _coinCounts.Remove(dead[i]); }
            } catch { }
        }

        //本地金币：EX 加金币 → 增加 CoinsCollected（结算金币分块用）+ mod 独立显示计数，刷新头顶。
        private static void GiveCoinLocally(int target, int amount) {
            try {
                Character tc = GetCharacter(target);
                if (tc == null) return;
                tc.CoinsCollected += amount; //结算金币分块计数
                int total = 0;
                if (_coinCounts.TryGetValue(target, out total)) total += amount;
                else total = amount;
                _coinCounts[target] = total;
                RefreshCoinText(tc, total); //头顶只显示 EX 加的金币数
            } catch { }
        }

        //刷新角色头顶金币显示（显示 mod 独立计数，不包含拾取金币）
        private static void RefreshCoinText(Character c, int count) {
            try {
                if (c == null) return;
                if (c.CoinNumberText != null) {
                    c.CoinNumberText.text = count.ToString();
                    if (c.CoinCanvas != null) c.CoinCanvas.SetActive(count > 0);
                }
            } catch { }
        }

	}
}
