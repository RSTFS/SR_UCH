using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using SR_UCH.Tweaks; //SR / Loc

namespace SR_UCH_EX {
public partial class ExModule {

// ==== 分区：Target（目标玩家表 / 选择 / 踢人）====

        public static string TargetName() {
            try {
                List<int> nums = OnlineNumbers();
                if (nums.Count == 0) return "（无玩家）";
                if (_targetNumber < 0 || !nums.Contains(_targetNumber)) return "（未选择）";
                return PlayerName(_targetNumber);
            } catch { return "?"; }
        }

        public static int CurrentTargetNumber() {
            try {
                List<int> nums = OnlineNumbers();
                if (nums.Count == 0) return -1;
                if (_targetNumber < 0 || !nums.Contains(_targetNumber)) return -1;
                return _targetNumber;
            } catch { return -1; }
        }

        public class PlayerRow {
            public int number;
            public string animal;
            public int score;
        }

        private static ScoreKeeper ScoreKeeperInstance() {
            try {
                //字段缓存（P1-5）：原来每次调用都 AccessTools.Field
                if (_scoreKeeperInstanceField == null)
                    _scoreKeeperInstanceField = HarmonyLib.AccessTools.Field(typeof(ScoreKeeper), "instance");
                return _scoreKeeperInstanceField != null ? _scoreKeeperInstanceField.GetValue(null) as ScoreKeeper : null;
            } catch { return null; }
        }

        //确保目标 GamePlayer 在 ScoreKeeper.playerTotal 里（房客本地计分表可能没初始化到该玩家，
        //AddPointsDirectly 用 ContainsKey 会静默失败 → “给别的房客加分”必须先把目标登记进去）
        //反射目标缓存（#13）：原来每次都按字符串找 playerTotal / scoreInfo 及 4 个私有字段，
        //字段名一旦变动就**静默失效**（表现为“给房客加分没反应”且无任何日志）。这里缓存 + 缺失时告警一次。
        private static System.Reflection.FieldInfo _playerTotalField;
        private static System.Type _scoreInfoType;
        private static System.Reflection.FieldInfo _siTotal, _siLose, _siWin, _siDisc;
        private static bool _scoreInfoWarned;

        private static void EnsureInPlayerTotal(ScoreKeeper sk, GamePlayer gp) {
            try {
                if (sk == null || gp == null) return;
                if (_playerTotalField == null) _playerTotalField = HarmonyLib.AccessTools.Field(typeof(ScoreKeeper), "playerTotal");
                if (_playerTotalField == null) { WarnScoreInfoOnce("ScoreKeeper.playerTotal 字段未找到"); return; }
                System.Collections.IDictionary dict = _playerTotalField.GetValue(sk) as System.Collections.IDictionary;
                if (dict == null || dict.Contains(gp)) return;
                //scoreInfo 是 private struct：反射创建并登记
                if (_scoreInfoType == null) _scoreInfoType = typeof(ScoreKeeper).GetNestedType("scoreInfo", System.Reflection.BindingFlags.NonPublic);
                if (_scoreInfoType == null) { WarnScoreInfoOnce("ScoreKeeper.scoreInfo 类型未找到"); return; }
                if (_siTotal == null) _siTotal = _scoreInfoType.GetField("totalScore");
                if (_siLose == null) _siLose = _scoreInfoType.GetField("loseStreak");
                if (_siWin == null) _siWin = _scoreInfoType.GetField("winStreak");
                if (_siDisc == null) _siDisc = _scoreInfoType.GetField("disconnected");
                object si = System.Activator.CreateInstance(_scoreInfoType);
                try { if (_siTotal != null) _siTotal.SetValue(si, 0); } catch { }
                try { if (_siLose != null) _siLose.SetValue(si, 0); } catch { }
                try { if (_siWin != null) _siWin.SetValue(si, 0); } catch { }
                try { if (_siDisc != null) _siDisc.SetValue(si, false); } catch { }
                dict.Add(gp, si);
            } catch (Exception e) {
                WarnScoreInfoOnce("登记 playerTotal 失败: " + e.Message);
            }
        }

        private static void WarnScoreInfoOnce(string msg) {
            if (_scoreInfoWarned) return;
            _scoreInfoWarned = true;
            try { SR_UCH.MainPlugin.ModLogger.LogWarning("[EX] " + msg); } catch { }
        }

        //玩家表每帧缓存（P1-9）：EX 页每帧渲染都会调 PlayerTable，而每行要跑 GetCharacter
        //（末级还会 FindObjectsOfType<Character>()）。同一帧内复用同一份结果，开销从 N 次降到 1 次。
        private static List<PlayerRow> _tableCache;
        private static int _tableCacheFrame = -1;

        public static List<PlayerRow> PlayerTable() {
            if (_tableCache != null && _tableCacheFrame == Time.frameCount) return _tableCache;
            List<PlayerRow> rows = new List<PlayerRow>();
            try {
                foreach (int n in OnlineNumbers()) {
                    PlayerRow row = new PlayerRow();
                    row.number = n;
                    row.animal = AnimalName(n);
                    row.score = GetScore(n);
                    rows.Add(row);
                }
            } catch { }
            _tableCache = rows;
            _tableCacheFrame = Time.frameCount;
            return rows;
        }

        private static string AnimalName(int number) {
            try {
                Character c = GetCharacter(number);
                if (c != null) return AnimalNameZh(c.NetworkCharacterSprite);
            } catch { }
            return "?";
        }

        private static int GetScore(int number) {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.PlayerTracker == null) return 0;
                //ScoreKeeperInstance() 只取一次（原来判空和取值各调一次，2×N 次反射 — P1-5）
                ScoreKeeper sk = ScoreKeeperInstance();
                if (sk == null) return 0;
                GamePlayer gp = lm.PlayerTracker.GetGamePlayer(number);
                if (gp != null) return sk.GetPlayerTotal(gp);
            } catch { }
            return 0;
        }

        private static string AnimalNameZh(Character.Animals a) {
            try { return SR.EnumNameZh(a.ToString()); } catch { return a.ToString(); }
        }

        private static int CurrentTarget() {
            List<int> nums = OnlineNumbers();
            if (nums.Count == 0) {
                Notify("未在关卡内");
                return -1;
            }
            //目标存 networkNumber：只要它仍在线就有效（玩家进出重排不会指错人 — P0-2）
            if (_targetNumber < 0 || !nums.Contains(_targetNumber)) {
                Notify("请先在表格中选择目标");
                return -1;
            }
            return _targetNumber;
        }

        public static void KickTarget() {
            List<int> nums = OnlineNumbers();
            if (nums.Count == 0) {
                Notify("未在关卡内");
                return;
            }
            if (_targetNumber < 0 || !nums.Contains(_targetNumber)) {
                Notify("请先在表格中选择目标");
                return;
            }
            int target = _targetNumber;
            int me = LocalNumber();
            if (target == me) {
                Notify("不能踢自己");
                return;
            }
            LobbyManager lm = LobbyManager.instance;
            if (lm == null) {
                Notify("未在关卡内");
                return;
            }
            try {
                bool isHost = false;
                try { isHost = lm.IsHost; } catch { }
                if (isHost) {
                    //host: use the official broadcast path
                    lm.IssueKickMessage(target, LobbyManager.KickReasons.HOST);
                } else {
                    //guest: forge a ClientKicked message to the host, who executes the kick
                    if (lm.client == null) {
                        Notify("未在关卡内");
                        return;
                    }
                    MsgClientKicked msg = new MsgClientKicked {
                        NetworkPlayerNumber = target,
                        kickReason = LobbyManager.KickReasons.HOST
                    };
                    lm.client.Send(NetMsgTypes.ClientKicked, msg);
                }
                Notify("已踢: " + PlayerName(target));
            } catch (Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("踢人失败: " + e.Message);
            }
        }

        public static void SelectTargetByIndex(int index) {
            try {
                List<int> nums = OnlineNumbers();
                if (nums.Count == 0) return;
                if (index < 0 || index >= nums.Count) return;
                _targetNumber = nums[index]; //UI 仍按行下标调用；这里转成 networkNumber 存起来（P0-2）
                Notify("目标: " + PlayerName(_targetNumber));
            } catch { }
        }

        //供 SR_UCH 的 EX 页判断某玩家号是否为本地玩家（表格行显示「（我）」标记，P0-4）。
        //签名必须严格为 public static bool IsSelf(int)：ExRef.Call("IsSelf", number) 按实参类型
        //typeof(int) 查找，签名不符反射会返回 null → 标记永远不显示且静默无报错。
        public static bool IsSelf(int number) {
            try { return number >= 0 && number == LocalNumber(); } catch { return false; }
        }

	}
}
