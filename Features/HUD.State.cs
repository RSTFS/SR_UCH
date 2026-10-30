// HUD 状态更新分片（HUD.cs = 配置/注册/设置页；本文件 = 每帧状态与回合/关卡读数；绘制仍在 HUD.cs 的 DrawHud）
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class HUD {

        private static void OnGameControlEvent(GameControl __instance, global::GameEvent.GameEvent e) {
            try {
                if (__instance == null || e == null) return;
                StartPhaseEvent start = e as StartPhaseEvent;
                if (start != null) { OnPhaseStarted(__instance, start.Phase); return; }
                EndPhaseEvent end = e as EndPhaseEvent;
                if (end != null) { OnPhaseEnded(end.Phase); return; }
                ScoreboardEvent sb = e as ScoreboardEvent;
                if (sb != null) {
                    // 记分板出现/结算完成 = 本回合确实推进过，可作为“真实回合”增量的证据。
                    try { if (sb.Showing || sb.AfterTally) _roundTallySeen = true; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
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
                    _trueRound = 0;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                LobbyManager lm = LobbyManager.instance;
                GameControl gc = (lm != null) ? lm.CurrentGameController : null;
                if (gc == null) {
                    // 树屋/大厅：HUD 完全不显示（用户要求删除大厅显示）。
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
                    _trueRound = 0;
                    _roundPending = 0;
                    _roundTallySeen = false;
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
            bool keyVis;
            try {
                if (SRApi.UiOpen) keyVis = true;
                else if (ModeValue() == ShowMode.Hold) keyVis = SRApi.ComboKeyHeld(_showKey);
                else {
                    if (SRApi.ComboKeyDown(_showKey)) _toggleOn = !_toggleOn;
                    keyVis = _toggleOn;
                }
            } catch { keyVis = false; }
            StepAlpha(IsEnabled() && (_hasGame || _inLobby) && keyVis);
        }
        private static void StepAlpha(bool target) {
            float want = target ? 1f : 0f;
            bool fade = FadeOn();
            if (target && !_wasVisible) _hudShownAt = Time.unscaledTime;
            _wasVisible = target;
            if (fade) {
                _hudAppear = Mathf.MoveTowards(_hudAppear, want, 4f * Time.unscaledDeltaTime);
                _hudAlpha = EaseOut(_hudAppear);   // alpha 也用 ease-out
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
            // 挑战模式：计时以游戏自己的 runTime 为准，按空格开始前（runStarted=false）不计数。
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
                // 派对模式：建造阶段以游戏自己的 placementTimer 为准（每个派对盒/每次进入建造都会重置）。
                if (IsPartyMode(gs)) {
                    if (vs != null) {
                        float pt = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "HUD（建造剩余时间）");
                        if (pt >= 0f) { countdown = true; remaining = pt; break; }
                    }
                    float partyTotal = gs != null ? gs.PlaceTime : 0f;
                    if (partyTotal > 0.05f) { countdown = true; remaining = partyTotal - elapsed; }
                    break;
                }
                bool useTimer = gs != null && gs.UsePlaceTimer;
                if (useTimer) {
                    if (vs != null) {
                        float t = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "HUD（建造剩余时间）");
                        if (t >= 0f) {
                            if (t > 0.01f) _vsPlaceSeen = true;
                            if (_vsPlaceSeen) { countdown = true; remaining = t; break; }
                        }
                    }
                    float total = gs != null ? gs.PlaceTime : 0f;
                    if (total > 0.05f) { countdown = true; remaining = total - elapsed; }
                }
                break;
            }
            case GameControl.GamePhase.PLAY: {
                if (gs != null && gs.GameLimitType == GameLimitType.TIME && vs != null) {
                    float used = ReadFloat(ref _fLevelTimer, vs, "levelTimer", "HUD（对局已用时间）");
                    if (used >= 0f) { countdown = true; remaining = (float)gs.MaxTime - used; }
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
            return IsChallengeMode() ? SRApi.T("重试：", "Retry: ") : SRApi.T("回合：", "Round: ");
        }
        private static void UpdateRound(GameControl gc, GameSettings gs) {
            try {
                int candidate = gc.RoundNumber;
                // 挑战模式：每死一次 = 一次重试，没有「回合上限」概念，也不发结算记分板事件。
                // 直接采用游戏自身的 roundNumber（避免被“结算证据”门槛卡住），只显示「重试：N」。
                if (IsChallengeMode()) {
                    if (candidate <= 0) { _roundText = null; return; }
                    _trueRound = candidate;
                    _roundPending = 0;
                    _roundTallySeen = false;
                    _roundText = RoundWord() + candidate;
                    return;
                }
                if (candidate < _trueRound) {
                    // 新对局/重载回退：以较小值重新开始
                    _trueRound = candidate;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                if (_trueRound <= 0 && candidate > 0) {
                    // 首回合直接接受
                    _trueRound = candidate;
                    _roundPending = 0;
                } else if (candidate > _trueRound) {
                    if (candidate == _trueRound + 1 && _roundTallySeen) {
                        _trueRound = candidate;
                        _roundPending = 0;
                        _roundTallySeen = false;
                    } else if (candidate > _roundPending) {
                        _roundPending = candidate;   // 未确认（可能是重载/刷新派对盒的额外 +1）
                    }
                }
                if (_roundTallySeen && _roundPending > _trueRound) {
                    // 结算已发生：接受此前挂起的真实回合
                    _trueRound = _roundPending;
                    _roundPending = 0;
                    _roundTallySeen = false;
                }
                if (_trueRound <= 0) { _roundText = null; return; }
                int max = 0;
                try { if (gs != null) max = gs.MaxRounds; } catch { max = 0; }
                int shown = max > 0 ? Mathf.Min(_trueRound, max) : _trueRound;
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
            if (string.IsNullOrEmpty(level) && !string.IsNullOrEmpty(code)) level = SRApi.T("自定义关卡", "Custom level");
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
            _winText = SRApi.T("获胜：", "Win: ") + target;
        }
        private static void UpdateRoundNotice() {
            try {
                int round = _trueRound;
                if (round <= 0 || round == _lastRound) return;
                _lastRound = round;
                if (!RoundNoticeOn()) return;
                _roundNoticeText = RoundWord() + round;
                _roundNoticeStart = Time.unscaledTime;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
        }
        private static LevelPortal FindLeadingPortal(LobbyManager lm, out int votes) {
            votes = 0;
            try {
                if (lm == null) return null;
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                if (lsc == null) return null;
                LevelPortal best = null;
                int bestVotes = 0;
                if (lsc.portals != null) {
                    for (int i = 0; i < lsc.portals.Length; i++) {
                        LevelPortal p = lsc.portals[i];
                        if (p == null || p.Votes == null) continue;
                        if (p.Votes.Count > bestVotes) { bestVotes = p.Votes.Count; best = p; }
                    }
                }
                if (lsc.snapshotPortals != null) {
                    for (int i = 0; i < lsc.snapshotPortals.Length; i++) {
                        LevelPortal p = lsc.snapshotPortals[i];
                        if (p == null || p.Votes == null) continue;
                        if (p.Votes.Count > bestVotes) { bestVotes = p.Votes.Count; best = p; }
                    }
                }
                votes = bestVotes;
                return best;
            } catch { return null; }
        }
        private static string PortalName(LevelPortal p) {
            try {
                CustomLevelPortal cp = p as CustomLevelPortal;
                if (cp != null && cp.snapshotInfo != null && !string.IsNullOrEmpty(cp.snapshotInfo.snapshotName)) {
                    return Truncate(cp.snapshotInfo.snapshotName, 16);
                }
                if (p.TargetLevel == GameState.LevelName.RANDOM) return SRApi.T("随机", "Random");
                string lv = GameState.GetLevelLocalizedName(p.TargetLevel);
                if (string.IsNullOrEmpty(lv) || lv.IndexOf("default Level name error", StringComparison.Ordinal) >= 0) {
                    lv = p.TargetLevel.ToString();
                }
                return Truncate(lv, 16);
            } catch { return null; }
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
        private static string Truncate(string s, int max) {
            if (string.IsNullOrEmpty(s)) return s;
            if (max <= 3 || s.Length <= max) return s;
            return s.Substring(0, max - 2) + "..";
        }
        private static string ModeName(GameState.GameMode mode) {
            switch (mode) {
            case GameState.GameMode.FREEPLAY: return SRApi.T("自由", "Freeplay");
            case GameState.GameMode.CREATIVE: return SRApi.T("创造", "Creative");
            case GameState.GameMode.PARTY: return SRApi.T("派对", "Party");
            case GameState.GameMode.CHALLENGE: return SRApi.T("挑战", "Challenge");
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
            case GameControl.GamePhase.START: return SRApi.T("准备", "Ready");
            case GameControl.GamePhase.PLAY: return SRApi.T("游玩", "Play");
            case GameControl.GamePhase.PLACE: return SRApi.T("建造", "Build");
            case GameControl.GamePhase.SUDDENDEATH: return SRApi.T("决胜", "Sudden death");
            case GameControl.GamePhase.END: return SRApi.T("结算", "End");
            case GameControl.GamePhase.WAIT: return SRApi.T("等待", "Wait");
            default: return SRApi.T("空闲", "Idle");
            }
        }

}
}
