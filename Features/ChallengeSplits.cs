// 挑战分段计时：按金币（或重试）切分一局，和本地 PB 分段对比，并在屏幕上显示分段块。
// 数据来源：ChallengeControl.runTime（游戏自己的挑战计时）+ CoinPickupEvent（金币是挑战关的进度节点）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.challengesplits", "ChallengeSplits", "1.0")]
    public class ChallengeSplits : ITweak {
        private const string Sec = "Challenge";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _source;      // coins / retry
        private static ConfigEntry<bool> _show;
        private static ConfigEntry<int> _font;
        private static ConfigEntry<bool> _compare;
        private static ConfigEntry<string> _pbData;      // 内部状态：关卡=分段...;（勿手改）

        private static readonly List<float> _splits = new List<float>();
        private static readonly List<float> _pb = new List<float>();
        private static string _levelKey;                 // 当前关卡键（PB 索引）
        private static float _lastRun;                   // 上一次读到的 runTime（判断重置）
        private static float _maxRun;                    // 本局最大 runTime（重试分段用）
        private static float _shownAt;
        private static string _hudText;                  // 屏幕显示文本（每 0.1 秒重建一次）
        private static System.Reflection.FieldInfo _fRunTime;
        private static GameEvent.IGameEventListener _listener;
        private static GameObject _root;

        // ---------------- 注册 ----------------
        private static void SelfReg() {
            SRApi.LocSec(Sec, "挑战", null);
            SRApi.Nav(Sec, 36);
            SRApi.RegisterPage(Sec, Render);

            SRApi.LocKey(Sec, "Enabled", "分段计时", null);
            SRApi.LocDesc(Sec, "Enabled", "分段计时\\n挑战模式按金币把一局切成多段\\n与本地 PB 分段对比", "Split timing\\nsplits a challenge run at every coin\\ncompared against your local PB splits");
            SRApi.LocKey(Sec, "Split Source", "分段依据", null);
            SRApi.LocDesc(Sec, "Split Source", "分段依据\\ncoins=每拿一枚金币记一段\\nretry=每次重试记一段", "Split source\\ncoins = one split per coin picked up\\nretry = one split per retry");
            SRApi.LocKey(Sec, "Show Overlay", "屏幕显示", null);
            SRApi.LocDesc(Sec, "Show Overlay", "屏幕显示\\n在屏幕右上角显示分段与 PB 差\\n仅挑战模式", "On-screen block\\nshows splits and PB deltas at the top right\\nchallenge mode only");
            SRApi.LocKey(Sec, "Font Size", "字号", null);
            SRApi.LocDesc(Sec, "Font Size", "分段块字号\\n屏幕显示文字大小\\n范围 10-40", "Split font size\\ntext size of the on-screen block\\nrange 10-40");
            SRApi.LocKey(Sec, "PB Data", "PB 数据", null);
            SRApi.LocDesc(Sec, "PB Data", "PB 分段数据\n内部状态：每关的最佳分段与总时间\n由结算自动写入，勿手动修改", "PB split data\ninternal state: best splits and total per level\nwritten automatically at the results screen");
            SRApi.LocKey(Sec, "Compare PB", "对比 PB", null);
            SRApi.LocDesc(Sec, "Compare PB", "对比 PB\\n显示每段与本地最佳记录的差\\n没有 PB 时只显示用时", "Compare with PB\\nshows the delta to your local best per split\\nplain times when there is no PB");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Enabled", false, "挑战分段计时总开关。");
                _source = plugin.Config.Bind(Sec, "Split Source", "coins", "分段依据：coins = 每枚金币；retry = 每次重试。");
                _show = plugin.Config.Bind(Sec, "Show Overlay", true, "在屏幕右上角显示分段块（仅挑战模式）。");
                _font = plugin.Config.Bind(Sec, "Font Size", 18, new ConfigDescription("分段块字号（10-40）", new AcceptableValueRange<int>(10, 40)));
                _compare = plugin.Config.Bind(Sec, "Compare PB", true, "显示与本地 PB 分段的差值。");
                _pbData = plugin.Config.Bind(Sec, "PB Data", "", "内部状态：每关最佳分段与总时间（勿手动修改）。");
                SRApi.Ref.Override("ChallengeControl.runTime", "挑战分段（读取游戏计时）");
                SRApi.RegisterTick("挑战分段-采样", Tick);

                _listener = new CoinListener();
                GameEventManager.ChangeListener<CoinPickupEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEndEvent>(_listener, true);

                GameObject go = new GameObject("SR_UCHChallengeSplits");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Overlay>();
                _root = go;
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/初始化", __ex); }
        }

        // ---------------- 页面 ----------------
        private static void Render() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 挑战分段计时 -", "- Challenge splits -"),
                    SRApi.T("按金币或重试把一局切成多段，并与本地 PB 分段比较；数据只存在本机配置里。",
                            "Splits a run at coins or retries and compares against your local PB; data stays in the local config."));
                SRApi.RenderSectionEntries(Sec);
                GUILayout.Space(SRApi.Ctl.Sc(6f));
                GUILayout.BeginHorizontal();
                try {
                    if (GUILayout.Button(SRApi.T("清除本关 PB", "Clear level PB"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(140f)), GUILayout.Height(SRApi.Ctl.Sc(26f)))) {
                        ClearPb();
                    }
                    if (GUILayout.Button(SRApi.T("重置本局分段", "Reset splits"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(140f)), GUILayout.Height(SRApi.Ctl.Sc(26f)))) {
                        _splits.Clear(); _maxRun = 0f; _hudText = null;
                    }
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
                SRApi.Ctl.LabelAuto(string.IsNullOrEmpty(_hudText) ? SRApi.T("（本局还没有分段）", "(no splits yet)") : _hudText);
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/页面", __ex); }
        }

        // ---------------- 数据 ----------------
        private static bool InChallenge() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                return gs != null && gs.GameMode == GameState.GameMode.CHALLENGE;
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/模式判定", __ex); return false; }
        }

        private static float RunTime() {
            try {
                LobbyManager lm = LobbyManager.instance;
                ChallengeControl cc = lm != null ? lm.CurrentGameController as ChallengeControl : null;
                if (cc == null) return -1f;
                if (_fRunTime == null) _fRunTime = SRApi.Ref.Field(typeof(ChallengeControl), "runTime", "挑战分段（读取游戏计时）");
                if (_fRunTime == null) return -1f;
                object v = _fRunTime.GetValue(cc);
                return v is float ? (float)v : -1f;
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/读计时", __ex); return -1f; }
        }

        private static string LevelKey() {
            try {
                GameState gs = GameState.GetInstance();
                if (gs != null) return gs.SelectedLevel.ToString();
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/关卡键", __ex); }
            return "UNKNOWN";
        }

        // 新一局：runTime 明显回退（重试/重开）
        private static void Tick() {
            try {
                if (_enabled == null || !_enabled.Value) return;
                if (!SRApi.GateMaster || !InChallenge()) return;
                float run = RunTime();
                if (run < 0f) return;
                _levelKey = LevelKey();
                if (_lastRun > 0.5f && run + 0.5f < _lastRun) OnNewRun(_lastRun);
                _lastRun = run;
                if (run > _maxRun) _maxRun = run;
                if (_show != null && _show.Value && Time.unscaledTime - _shownAt >= 0.1f) { _shownAt = Time.unscaledTime; _hudText = BuildText(); }
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/采样", __ex); }
        }

        private static void OnNewRun(float total) {
            try {
                _splits.Clear();
                if (_source != null && _source.Value == "retry" && total > 0.05f) _splits.Add(total);
                _maxRun = 0f;
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/新一局", __ex); }
        }

        // 金币事件：coins 按"第 N 枚"记段，不受拿取顺序影响。
        private static void OnCoin(bool pickedUp) {
            try {
                if (!pickedUp || _enabled == null || !_enabled.Value) return;
                if (!SRApi.GateMaster || !InChallenge()) return;
                if (_source != null && _source.Value == "retry") return;
                float run = RunTime();
                if (run < 0f) return;
                float seg = run - (_splits.Count > 0 ? _splits[_splits.Count - 1] : 0f);
                if (seg <= 0f) return;
                _splits.Add(run);
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/金币事件", __ex); }
        }

        // 结算：整局完成时刷新 PB 分段
        private static void OnGameEnd() {
            try {
                if (_enabled == null || !_enabled.Value) return;
                if (!SRApi.GateMaster || !InChallenge()) return;
                if (_splits.Count == 0 || string.IsNullOrEmpty(_levelKey)) return;
                List<float> pb = LoadPb(_levelKey);
                if (pb.Count == 0 || _maxRun + 0.005f < pb[pb.Count - 1]) SavePb(_levelKey, _splits, _maxRun);
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/结算", __ex); }
        }

        private static void ClearPb() {
            try {
                string key = LevelKey();
                if (string.IsNullOrEmpty(key)) return;
                ConfigEntryBase e = SRApi.Ctl.FindEntry(Sec, "PB Data");
                if (e != null) {
                    string raw = e.BoxedValue as string ?? "";
                    List<string> keep = new List<string>();
                    foreach (string part in raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        if (!part.StartsWith(key + ":", StringComparison.Ordinal)) keep.Add(part);
                    SRApi.Ctl.SetValue(e, string.Join(";", keep.ToArray()));
                }
                SRApi.Notify(SRApi.T("已清除本关 PB", "Level PB cleared"));
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/清除 PB", __ex); }
        }

        private static void SavePb(string key, List<float> splits, float total) {
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry(Sec, "PB Data");
                if (e == null) return;
                string raw = e.BoxedValue as string ?? "";
                List<string> keep = new List<string>();
                foreach (string part in raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!part.StartsWith(key + ":", StringComparison.Ordinal)) keep.Add(part);
                List<string> vals = new List<string>();
                for (int i = 0; i < splits.Count; i++) vals.Add(splits[i].ToString("0.###"));
                vals.Add(total.ToString("0.###"));
                keep.Add(key + ":" + string.Join(",", vals.ToArray()));
                SRApi.Ctl.SetValue(e, string.Join(";", keep.ToArray()));
                SRApi.LogInfo("[挑战分段] 已记录 PB：" + key + " " + total.ToString("0.###"));
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/保存 PB", __ex); }
        }

        private static List<float> LoadPb(string key) {
            List<float> res = new List<float>();
            try {
                ConfigEntryBase e = SRApi.Ctl.FindEntry(Sec, "PB Data");
                if (e == null) return res;
                string raw = e.BoxedValue as string ?? "";
                foreach (string part in raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                    int c = part.IndexOf(':');
                    if (c <= 0 || part.Substring(0, c) != key) continue;
                    foreach (string v in part.Substring(c + 1).Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                        float f; if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) res.Add(f);
                    }
                    break;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/读取 PB", __ex); }
            return res;
        }

        private static string BuildText() {
            try {
                if (_splits.Count == 0) return SRApi.T("挑战：", "Challenge: ") + _maxRun.ToString("0.00") + "s";
                if (string.IsNullOrEmpty(_levelKey)) _levelKey = LevelKey();
                _pb.Clear();
                _pb.AddRange(LoadPb(_levelKey));
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append(SRApi.T("挑战分段", "Challenge splits")).Append("  ").Append(_maxRun.ToString("0.00")).Append("s");
                for (int i = 0; i < _splits.Count; i++) {
                    float seg = _splits[i] - (i > 0 ? _splits[i - 1] : 0f);
                    sb.Append('\n').Append('#').Append(i + 1).Append("  ").Append(seg.ToString("0.00"));
                    if (_compare != null && _compare.Value && i < _pb.Count) {
                        float pbSeg = _pb[i] - (i > 0 ? _pb[i - 1] : 0f);
                        float d = seg - pbSeg;
                        sb.Append("  ").Append(d >= 0f ? "+" : "").Append(d.ToString("0.00"));
                    }
                }
                return sb.ToString();
            } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/拼文本", __ex); return ""; }
        }

        private sealed class CoinListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    if (e is CoinPickupEvent) { OnCoin(((CoinPickupEvent)e).PickedUp); return; }
                    if (e is GameEndEvent) OnGameEnd();
                } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/事件", __ex); }
            }
        }

        // 屏幕右上角的分段块：自绘，不依赖 HUD 的排版，也不影响 HUD 的开关。
        private sealed class Overlay : MonoBehaviour {
            private GUIStyle _style;
            private int _styleFont = -1;
            private void OnGUI() {
                try {
                    if (_enabled == null || !_enabled.Value) return;
                    if (_show == null || !_show.Value) return;
                    if (!SRApi.UiOpen && !InChallenge()) return;
                    if (string.IsNullOrEmpty(_hudText)) return;
                    int f = _font != null ? Mathf.Clamp(_font.Value, 10, 40) : 18;
                    if (_style == null || _styleFont != f) {
                        _style = new GUIStyle(GUI.skin.label);
                        _style.fontSize = f;
                        _style.alignment = TextAnchor.UpperRight;
                        _style.font = GUI.skin.font;
                        _styleFont = f;
                    }
                    float w = Mathf.Min(Screen.width * 0.45f, 420f);
                    Rect r = new Rect(Screen.width - w - 12f, 12f, w, 24f * Mathf.Max(1, _hudText.Split('\n').Length) + 12f);
                    GUI.Label(r, _hudText, _style);
                } catch (Exception __ex) { SRApi.Guard.Log("挑战分段/绘制", __ex); }
            }
        }
    }
}
