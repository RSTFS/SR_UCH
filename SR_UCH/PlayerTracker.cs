using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //integrated port of the UCHPlayerTrackerMod: a trailing line behind each player
    public class PlayerTracker : ITweak {
        private static MainPlugin _mp;
        private static ConfigEntry<int> _trackingLength;
        private static ConfigEntry<int> _skipFrames;
        private static ConfigEntry<float> _lineWidthStart;
        private static ConfigEntry<float> _lineWidthEnd;
        private static ConfigEntry<float> _teleportThreshold;
        //runtime toggle (also controlled by the in-game manager)
        public static bool Enabled = true;

        private class LineInfo {
            public Queue<Vector3> queue = new Queue<Vector3>();
            public LineRenderer renderer;
            public GameObject go;
        }

        private struct PlayerLine {
            public Character character;
            public LineInfo line;
        }

        private static LineInfo[] _lines;
        private static GameObject _trackerRoot; //行对象的父节点（Initialize 里创建）
        //索引复用的字典：原来每物理帧 ToDictionary ≈50 次/秒分配（提示词第 7 节性能项）
        private static readonly Dictionary<int, GamePlayer> _gpByIndex = new Dictionary<int, GamePlayer>();
        private static readonly Dictionary<int, LobbyPlayer> _lpByIndex = new Dictionary<int, LobbyPlayer>();

        private class LevelResetListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                ClearLines();
            }
        }

        private class TrackerComponent : MonoBehaviour {
            public int framesLeft;
            private bool _lastEff;
            private void FixedUpdate() {
                if (!_linesReady()) return;
                bool eff = Enabled && SR.GateMaster;
                //只在显示状态变化时 SetActive（避免每帧对 8 条线重复调用）
                if (eff != _lastEff) {
                    _lastEff = eff;
                    foreach (LineInfo li in _lines) {
                        if (li.go != null && li.go.activeSelf != eff) li.go.SetActive(eff);
                    }
                }
                if (!eff) return;
                if (framesLeft > 0) { framesLeft--; return; }
                framesLeft = _skipFrames.Value;
                try {
                    foreach (PlayerLine pl in GetPlayers()) {
                        Vector3 pos = pl.character.transform.position;
                        //异常坐标过滤：与上一个记录点距离超过阈值（角色消失/被瞬移，如派对盒
                        //选道具时坐标跳到远处）→ 清空重来，避免拖出贯穿全图的长线。
                        //阈值可自定义（Teleport Threshold：超过该距离就删除已绘制线段重新开始）
                        float threshold = (_teleportThreshold != null) ? Mathf.Max(1f, _teleportThreshold.Value) : 40f;
                        if (pl.line.queue.Count > 0) {
                            float d = Vector3.Distance(pl.line.queue.Last(), pos);
                            if (d > threshold) {
                                //清空重来：只需把位置数置 0（不再分配空数组）
                                pl.line.queue.Clear();
                                pl.line.renderer.positionCount = 0;
                                continue;
                            }
                        }
                        pl.line.queue.Enqueue(pos);
                        while (pl.line.queue.Count > _trackingLength.Value) pl.line.queue.Dequeue();
                        //用 SetPosition 逐点写入（复用枚举器，避免每帧 queue.ToArray() 分配）
                        ApplyPositions(pl.line);
                    }
                } catch (Exception e) {
                    Debug.LogError(e.Message + e.StackTrace);
                }
            }
        }

        private static bool _linesReady() { return _lines != null; }

        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            ConfigEntry<bool> enabled = _mp.Config.Bind("Player Tracker", "Enabled", false, "总开关");
            Enabled = enabled.Value;
            enabled.SettingChanged += (s, e) => Enabled = enabled.Value;
            _trackingLength = _mp.Config.Bind(
                "Player Tracker",
                "Tracking Length",
                120,
                "The length of the line in timeSteps (60 -> 1s)");
            _skipFrames = _mp.Config.Bind(
                "Player Tracker",
                "Skip Frames",
                0,
                "Skip n frames before tracking next frame");
            _lineWidthStart = _mp.Config.Bind(
                "Player Tracker",
                "Line Start Width",
                0.1f,
                "Width of the tracking line at the start");
            _lineWidthEnd = _mp.Config.Bind(
                "Player Tracker",
                "Line End Width",
                0.1f,
                "Width of the tracking line at the end");
            _teleportThreshold = _mp.Config.Bind(
                "Player Tracker",
                "Teleport Threshold",
                10f,
                "Distance that clears the drawn trail (if a character teleports farther than this, delete the existing line and start over). Min 1.");

            //persistent object that owns the tracker update and the line renderers
            GameObject go = new GameObject("SR_UCHPlayerTracker");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TrackerComponent>();

            _trackerRoot = go;
            //行池按需增长（原来固定 8 条，超过 8 人——如 Even More Players——就没有轨迹线）
            EnsureCapacity(8);

            GameEventManager.ChangeListener<GameEvent.LevelResetEvent>(new LevelResetListener(), true);
        }

        private static void ClearLines() {
            if (!_linesReady()) return;
            foreach (LineInfo li in _lines) ClearLine(li);
        }

        //把队列里的点写进 LineRenderer：逐点 SetPosition，避免 queue.ToArray() 的每帧分配
        private static void ApplyPositions(LineInfo line) {
            int n = line.queue.Count;
            line.renderer.positionCount = n;
            if (n == 0) return;
            int i = 0;
            foreach (Vector3 v in line.queue) line.renderer.SetPosition(i++, v);
        }

        private static void ClearLine(LineInfo line) {
            if (line == null) return;
            line.queue.Clear();
            if (line.renderer != null) line.renderer.positionCount = 0;
        }

        //行池按需增长：支持超过 8 名玩家（Even More Players）。只在需要时创建新 GameObject。
        private static void EnsureCapacity(int count) {
            if (_lines != null && _lines.Length >= count) return;
            int target = Mathf.Max(count, _lines == null ? 8 : _lines.Length);
            LineInfo[] arr = new LineInfo[target];
            int old = _lines == null ? 0 : _lines.Length;
            if (old > 0) Array.Copy(_lines, arr, old);
            for (int i = old; i < target; i++) arr[i] = CreateLine(i);
            _lines = arr;
        }

        private static LineInfo CreateLine(int i) {
            LineInfo li = new LineInfo();
            li.go = new GameObject("TrackerLine" + i);
            if (_trackerRoot != null) li.go.transform.SetParent(_trackerRoot.transform);
            LineRenderer lr = li.go.AddComponent<LineRenderer>();
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startWidth = _lineWidthEnd.Value;
            lr.endWidth = _lineWidthStart.Value;
            lr.useWorldSpace = true;
            li.renderer = lr;
            return li;
        }

        private static IEnumerable<PlayerLine> GetPlayers() {
            LobbyManager lm = LobbyManager.instance;
            if (lm == null) yield break;
            if (lm.CurrentGameController != null) {
                //复用字典（不再 ToDictionary 每帧分配）；行池按最大玩家索引增长（支持 >8 人）
                _gpByIndex.Clear();
                int need = 0;
                foreach (GamePlayer gp in lm.CurrentGameController.CurrentPlayerQueue) {
                    if (gp == null) continue;
                    int idx = gp.networkNumber - 1;
                    if (idx < 0) continue;
                    _gpByIndex[idx] = gp;
                    if (idx + 1 > need) need = idx + 1;
                }
                EnsureCapacity(need);
                for (int i = 0; i < _lines.Length; i++) {
                    GamePlayer gp;
                    if (!_gpByIndex.TryGetValue(i, out gp) || gp == null || gp.CharacterInstance == null) { ClearLine(_lines[i]); continue; }
                    _lines[i].renderer.startColor = gp.PlayerColor;
                    _lines[i].renderer.endColor = gp.PlayerColor;
                    yield return new PlayerLine { character = gp.CharacterInstance, line = _lines[i] };
                }
            } else {
                _lpByIndex.Clear();
                int need = 0;
                foreach (LobbyPlayer lp in lm.GetLobbyPlayers()) {
                    if (lp == null) continue;
                    int idx = lp.networkNumber - 1;
                    if (idx < 0) continue;
                    _lpByIndex[idx] = lp;
                    if (idx + 1 > need) need = idx + 1;
                }
                EnsureCapacity(need);
                for (int i = 0; i < _lines.Length; i++) {
                    LobbyPlayer lp;
                    if (!_lpByIndex.TryGetValue(i, out lp) || lp == null || lp.CharacterInstance == null) { ClearLine(_lines[i]); continue; }
                    _lines[i].renderer.startColor = lp.PlayerColor;
                    _lines[i].renderer.endColor = lp.PlayerColor;
                    yield return new PlayerLine { character = lp.CharacterInstance, line = _lines[i] };
                }
            }
        }
    }
}
