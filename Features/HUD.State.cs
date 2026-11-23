// HUD 状态分片：每帧状态与回合/关卡读数；配置与绘制见 HUD.cs。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class HUD {

        // 全局事件入口：相位切换与记分板事件都从这里分发。
        [HarmonyPatch(typeof(GameControl), "handleEvent")]
        [HarmonyPostfix]
        private static void OnGameControlEvent(GameControl __instance, global::GameEvent.GameEvent e) {
            try {
                if (__instance == null || e == null) return;
                StartPhaseEvent start = e as StartPhaseEvent;
                if (start != null) { OnPhaseStarted(__instance, start.Phase); return; }
                EndPhaseEvent end = e as EndPhaseEvent;
                if (end != null) { OnPhaseEnded(end.Phase); return; }
                ScoreboardEvent sb = e as ScoreboardEvent;
                if (sb != null) {
                    // 记分板出现或结算完成 = 本回合确实推进过，作为回合增量证据。
                    try { if (sb.Showing || sb.AfterTally) SR_UCH.Pure.RoundTruth.NoteTally(ref _rs); }
                    catch { /* 静默：非关键路径，失败可直接忽略 */ }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("HUD 阶段事件", __ex); }
        }
        private static void OnPhaseStarted(GameControl gc, GameControl.GamePhase phase) {
            _gc = gc;
            _eventSeen = true;
            _phase = phase;
            _phaseStart = Time.unscaledTime;
            _vsPlaceSeen = false;
        }
        private static void OnPhaseEnded(GameControl.GamePhase phase) {
            if (phase != _phase) return;
            if (Time.unscaledTime < _phaseStart) _phaseStart = Time.unscaledTime;
        }
        private static void Tick() {
            try {
                if (!IsEnabled()) {
                    _hasGame = false;
                    _inLobby = false;
                    _wasEnabled = false;
                    StepAlpha(false);
                    return;
                }
                if (!_wasEnabled) {
                    _wasEnabled = true;
                    _eventSeen = false;
                    _phase = GameControl.GamePhase.NONE;
                    _phaseStart = Time.unscaledTime;
                    _lastRound = 0;
                    SR_UCH.Pure.RoundTruth.Reset(ref _rs);
                }
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = (lm != null) ? lm.CurrentGameController : null;
                if (gc == null) {
                    // 树屋/大厅不显示 HUD。
                    if (_gc != null || _hasGame) ResetGameState();
                    _inLobby = false;
                    
                    StepAlpha(false);
                    return;
                }
                _inLobby = false;
                if (!ReferenceEquals(gc, _gc)) {
                    _gc = gc;
                    _phase = GameControl.GamePhase.NONE;
                    _phaseStart = Time.unscaledTime;
                    _eventSeen = false;
                    _vsPlaceSeen = false;
                    _timerText = "00:00";
                    _warnLevel = 0;
                    _lastRound = 0;
                    SR_UCH.Pure.RoundTruth.Reset(ref _rs);
                }
                if (!_eventSeen) {
                    GameControl.GamePhase p = gc.Phase;
                    if (p != GameControl.GamePhase.NONE && p != _phase) {
                        _phase = p;
                        _phaseStart = Time.unscaledTime;
                    }
                }
                if (_phase == GameControl.GamePhase.NONE) { _hasGame = false; StepAlpha(false); return; }
                _hasGame = true;

                GameSettings gs = ReadSettings();
                UpdateTimer(gc, gs);
                UpdateRound(gc, gs);
                UpdateLevel(gs);
                UpdateWin(gs);
                UpdateExtraText();
                UpdateRoundNotice();
                UpdateVisible();
            } catch (Exception __ex) { SRApi.Guard.Log("HUD 状态更新", __ex); }
        }
        private static void UpdateVisible() {
            // 由「显示 HUD」开关决定是否显示。
            bool keyVis = true;
            StepAlpha(IsEnabled() && (_hasGame || _inLobby) && keyVis);
        }
        private static void StepAlpha(bool target) {
            float want = target ? 1f : 0f;
            bool fade = FadeOn();
            if (target && !_wasVisible) _hudShownAt = Time.unscaledTime;
            _wasVisible = target;
            if (fade) {
                _hudAppear = Mathf.MoveTowards(_hudAppear, want, 4f * Time.unscaledDeltaTime);
                _hudAlpha = EaseOut(_hudAppear);   // alpha 同样 ease-out
            } else {
                _hudAppear = want;
                _hudAlpha = want;
            }
        }
        private static float EaseOut(float x) {
            float inv = 1f - Mathf.Clamp01(x);
            return 1f - inv * inv * inv;
        }
        private static float StaggerAlpha(int index) {
            if (_hudShownAt <= 0f) return 1f;
            float x = (Time.unscaledTime - _hudShownAt - index * 0.02f) / 0.2f;
            return EaseOut(x);
        }
        private static float StaggerOffset(int index) {
            if (_hudShownAt <= 0f) return 1f;
            float x = (Time.unscaledTime - _hudShownAt - index * 0.02f) / 0.25f;
            return 1f - EaseOut(x);
        }
        private static GameSettings ReadSettings() {
            try { return GameSettings.GetInstance(); } catch { return null; }
        }
        private static void UpdateTimer(GameControl gc, GameSettings gs) {
            // 挑战模式以游戏 runTime 为准；开始前（runStarted=false）不计数。
            if (IsChallengeMode() && gc is ChallengeControl) { UpdateChallengeTimer(gc); return; }

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _phaseStart);
            float remaining = -1f;
            bool countdown = false;
            VersusControl vs = gc as VersusControl;
            switch (_phase) {
            case GameControl.GamePhase.START: {
                float d = gc.StartDelay;
                if (d > 0.05f && d < 3600f) { countdown = true; remaining = d - elapsed; }
                break;
            }
            case GameControl.GamePhase.PLACE: {
                // 派对模式建造阶段以 placementTimer 为准（每个派对盒会重置）。
                if (IsPartyMode(gs)) {
                    if (vs != null) {
                        float pt = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "HUD（建造剩余时间）");
                        if (pt >= 0f) { countdown = true; remaining = pt; break; }
                    }
                    // 仅在 placementTimer 可读（真的在计时）时显示倒计时。
                    break;
                }
                // 建造倒计时：优先 placementTimer（暂停不减），再用 PlaceTime 兜底，取较小者。
                float placeLeft = -1f;
                if (vs != null) {
                    float t = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "HUD（建造剩余时间）");
                    if (t >= 0f) {
                        if (t > 0.01f) _vsPlaceSeen = true;
                        if (_vsPlaceSeen) placeLeft = t;
                    }
                }
                // 兜底仅用于已开始过建造计时（_vsPlaceSeen）的场景：否则 PlaceTime 只是配置值。
                if (gs != null && gs.UsePlaceTimer && _vsPlaceSeen) {
                    float total = gs.PlaceTime;
                    if (total > 0.05f) {
                        float byElapsed = total - elapsed;
                        placeLeft = placeLeft > 0f ? Mathf.Min(placeLeft, byElapsed) : byElapsed;
                    }
                }
                // 整场限时在建造阶段也在走，取较小者才不会误导。
                float totalLeft2 = -1f;
                if (gs != null && gs.GameLimitType == GameLimitType.TIME && vs != null) {
                    float used = ReadFloat(ref _fLevelTimer, vs, "levelTimer", "HUD（对局已用时间）");
                    if (used >= 0f) totalLeft2 = (float)gs.MaxTime - used;
                }
                if (placeLeft > 0f) { countdown = true; remaining = placeLeft; }
                if (totalLeft2 > 0f && (!countdown || totalLeft2 < remaining)) { countdown = true; remaining = totalLeft2; }
                break;
            }
            case GameControl.GamePhase.PLAY: {
                if (vs != null) {
                    // 每回合限时（RunTimerLimit）以游戏 runTimer 为准，它已处理回合起点与暂停。
                    float runLeft = -1f;
                    try { if (vs.runTimer != null) runLeft = vs.runTimer.TimeLeft; }
                    catch (Exception __rt) { SRApi.Guard.Log("HUD/读每回合剩余时间", __rt); }
                    if (runLeft > 0.01f) { countdown = true; remaining = runLeft; }
                    // 整场限时（GameLimitType.TIME）：levelTimer 全阶段累计，与每回合限时取较小者。
                    if (gs != null && gs.GameLimitType == GameLimitType.TIME) {
                        float used = ReadFloat(ref _fLevelTimer, vs, "levelTimer", "HUD（对局已用时间）");
                        if (used >= 0f) {
                            float totalLeft = (float)gs.MaxTime - used;
                            if (!countdown || totalLeft < remaining) { countdown = true; remaining = totalLeft; }
                        }
                    }
                }
                break;
            }
            case GameControl.GamePhase.SUDDENDEATH: {
                if (vs != null) {
                    float t = ReadFloat(ref _fCountdownTimer, vs, "countdownTimer", "HUD（决胜倒计时）");
                    if (t > 0.01f) { countdown = true; remaining = t; }
                }
                break;
            }
            case GameControl.GamePhase.END: {
                float d = gc.WinTime;
                if (d > 0.05f && d < 3600f) { countdown = true; remaining = d - elapsed; }
                break;
            }
            default:
                break;
            }
            if (countdown) {
                if (remaining < 0f) remaining = 0f;
                _timerText = FormatClock(Mathf.CeilToInt(remaining));
                float warn = WarnSeconds();
                float red = Mathf.Min(5f, warn);
                _warnLevel = remaining <= red ? 2 : (remaining <= warn ? 1 : 0);
            } else {
                _timerText = FormatClock(Mathf.FloorToInt(elapsed));
                _warnLevel = 0;
            }
        }
        private static bool IsChallengeMode() {
            try { GameSettings gs = GameSettings.GetInstance(); return gs != null && gs.GameMode == GameState.GameMode.CHALLENGE; }
            catch { return false; }
        }
        private static bool IsPartyMode(GameSettings gs) {
            try {
                GameSettings s = gs != null ? gs : GameSettings.GetInstance();
                return s != null && s.GameMode == GameState.GameMode.PARTY;
            } catch { return false; }
        }
        private static string RoundWord() {
            return IsChallengeMode() ? SRApi.Tid("e473619c63", "重试：", "Retry: ") : SRApi.Tid("cd5993f3eb", "回合：", "Round: ");
        }
        private static void UpdateRound(GameControl gc, GameSettings gs) {
            try {
                int candidate = gc.RoundNumber;
                // 真回合判定与混乱牌组共用 Pure/RoundTruth.cs：结算证据 + 挂起候选 + 只进不退。
                bool challenge = IsChallengeMode();
                SR_UCH.Pure.RoundTruth.Step(ref _rs, candidate, challenge);
                int trueRound = _rs.TrueRound;
                if (trueRound <= 0) { _roundText = null; return; }
                // 挑战模式：每死一次即一次重试，无回合上限，也不发结算记分板事件。
                if (challenge) { _roundText = RoundWord() + trueRound; return; }
                int max = 0;
                try { if (gs != null) max = gs.MaxRounds; } catch { max = 0; }
                int shown = max > 0 ? Mathf.Min(trueRound, max) : trueRound;
                _roundText = max > 0
                    ? RoundWord() + shown + "/" + max
                    : RoundWord() + shown;
            } catch { _roundText = null; }
        }
        private static string CurrentLevelName(out string code) {
            code = null;
            string level = null;
            try {
                GameState st = GameState.GetInstance();
                if (st != null) {
                    try {
                        level = st.currentSnapshotInfo.snapshotName;
                        code = st.currentSnapshotInfo.snapshotCode;
                    } catch { level = null; code = null; }
                    if (string.IsNullOrEmpty(level)) {
                        string lv = GameState.GetLevelLocalizedName(st.SelectedLevel);
                        if (!string.IsNullOrEmpty(lv) && lv.IndexOf("default Level name error", StringComparison.Ordinal) < 0) level = lv;
                    }
                }
            } catch { level = null; code = null; }
            if (string.IsNullOrEmpty(level) && !string.IsNullOrEmpty(code)) level = SRApi.Tid("756f1de77d", "自定义关卡", "Custom level");
            return level;
        }
        private static void UpdateLevel(GameSettings gs) {
            try {
                string code;
                string level = CurrentLevelName(out code);
                string mode = (gs != null) ? ModeName(gs.GameMode) : null;
                if (!string.IsNullOrEmpty(level) && !string.IsNullOrEmpty(mode)) _levelText = level + " · " + mode;
                else if (!string.IsNullOrEmpty(level)) _levelText = level;
                else if (!string.IsNullOrEmpty(mode)) _levelText = mode;
                else _levelText = null;
            } catch { _levelText = null; }
        }
        private static void UpdateWin(GameSettings gs) {
            int target = WinScoreFallback;
            try {
                if (gs != null) {
                    int ms = gs.MaxScore;
                    if (ms > 0) target = ms;
                }
            } catch { target = WinScoreFallback; }
            _winText = SRApi.Tid("967826caf4", "获胜：", "Win: ") + target;
        }
        private static void UpdateRoundNotice() {
            try {
                int round = _rs.TrueRound;
                if (round <= 0 || round == _lastRound) return;
                _lastRound = round;
                if (!RoundNoticeOn()) return;
                _roundNoticeText = RoundWord() + round;
                _roundNoticeStart = Time.unscaledTime;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }
        private static void UpdateExtraText() {
            _extra1Text = ReadExtraLine(_extra1);
            _extra2Text = ReadExtraLine(_extra2);
        }
        private static string ReadExtraLine(ConfigEntry<string> entry) {
            try {
                string s = entry != null ? entry.Value : null;
                if (string.IsNullOrEmpty(s)) return null;
                s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                if (s.Length > 120) s = s.Substring(0, 120);
                return s.Length > 0 ? s : null;
            } catch { return null; }
        }
        private static string ModeName(GameState.GameMode mode) {
            switch (mode) {
            case GameState.GameMode.FREEPLAY: return SRApi.Tid("ebb2dba278", "自由", "Freeplay");
            case GameState.GameMode.CREATIVE: return SRApi.Tid("e801e3fee7", "创造", "Creative");
            case GameState.GameMode.PARTY: return SRApi.Tid("97c0e6938b", "派对", "Party");
            case GameState.GameMode.CHALLENGE: return SRApi.Tid("0cce1e0303", "挑战", "Challenge");
            default: return null;
            }
        }
        private static float ReadFloat(ref FieldInfo f, object owner, string name, string feature) {
            if (owner == null) return -1f;
            if (f == null) {
                try { f = SRApi.Ref.Field(typeof(VersusControl), name, feature); } catch { f = null; }
            }
            if (f == null) return -1f;
            try {
                object v = f.GetValue(owner);
                return (v is float) ? (float)v : -1f;
            } catch { return -1f; }
        }
        private static void UpdateChallengeTimer(GameControl gc) {
            try {
                ChallengeControl cc = gc as ChallengeControl;
                float run = -1f;
                bool started = false;
                if (cc != null) {
                    run = ReadFloatOn(ref _fChallengeRunTime, typeof(ChallengeControl), cc, "runTime", "HUD（挑战计时）");
                    started = ReadBoolOn(ref _fChallengeRunStarted, typeof(ChallengeControl), cc, "runStarted", "HUD（挑战是否已开始）");
                }
                if (run < 0f) {
                    float elapsed = Mathf.Max(0f, Time.unscaledTime - _phaseStart);
                    _timerText = FormatClock(started ? Mathf.FloorToInt(elapsed) : 0);
                } else {
                    _timerText = FormatClock(Mathf.FloorToInt(started ? run : 0f));
                }
                _warnLevel = 0;
            } catch (Exception __ex) { SRApi.Guard.Log("HUD/挑战计时", __ex); _timerText = null; }
        }
        private static float ReadFloatOn(ref FieldInfo f, System.Type ownerType, object owner, string name, string feature) {
            if (owner == null || ownerType == null) return -1f;
            if (f == null) { try { f = SRApi.Ref.Field(ownerType, name, feature); } catch { f = null; } }
            if (f == null) return -1f;
            try { object v = f.GetValue(owner); return (v is float) ? (float)v : -1f; } catch { return -1f; }
        }
        private static bool ReadBoolOn(ref FieldInfo f, System.Type ownerType, object owner, string name, string feature) {
            if (owner == null || ownerType == null) return false;
            if (f == null) { try { f = SRApi.Ref.Field(ownerType, name, feature); } catch { f = null; } }
            if (f == null) return false;
            try { object v = f.GetValue(owner); return (v is bool) && (bool)v; } catch { return false; }
        }
        private static string FormatClock(int totalSeconds) {
            if (totalSeconds < 0) totalSeconds = 0;
            int m = totalSeconds / 60;
            int s = totalSeconds % 60;
            return m.ToString("00") + ":" + s.ToString("00");
        }
        private static string PhaseName(GameControl.GamePhase p) {
            switch (p) {
            case GameControl.GamePhase.START: return SRApi.Tid("87d63f318f", "准备", "Ready");
            case GameControl.GamePhase.PLAY: return SRApi.Tid("ff8579f723", "游玩", "Play");
            case GameControl.GamePhase.PLACE: return SRApi.Tid("9db72ef8ce", "建造", "Build");
            case GameControl.GamePhase.SUDDENDEATH: return SRApi.Tid("d8d8cb5185", "决胜", "Sudden death");
            case GameControl.GamePhase.END: return SRApi.Tid("b09c26afe2", "结算", "End");
            case GameControl.GamePhase.WAIT: return SRApi.Tid("df7f30218f", "等待", "Wait");
            default: return SRApi.Tid("25bf134c48", "空闲", "Idle");
            }
        }

}
}
