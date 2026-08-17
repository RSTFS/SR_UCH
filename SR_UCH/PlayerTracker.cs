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
                bool eff = Enabled && ModManager.AllEnabled;
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
                        pl.line.queue.Enqueue(pl.character.transform.position);
                        while (pl.line.queue.Count > _trackingLength.Value) pl.line.queue.Dequeue();
                        pl.line.renderer.positionCount = pl.line.queue.Count;
                        pl.line.renderer.SetPositions(pl.line.queue.ToArray());
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

            //persistent object that owns the tracker update and the line renderers
            GameObject go = new GameObject("SR_UCHPlayerTracker");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TrackerComponent>();

            _lines = new LineInfo[8];
            for (int i = 0; i < _lines.Length; i++) {
                _lines[i] = new LineInfo();
                _lines[i].go = new GameObject("TrackerLine" + i);
                _lines[i].go.transform.SetParent(go.transform);
                LineRenderer lr = _lines[i].go.AddComponent<LineRenderer>();
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startWidth = _lineWidthEnd.Value;
                lr.endWidth = _lineWidthStart.Value;
                lr.useWorldSpace = true;
                _lines[i].renderer = lr;
            }

            GameEventManager.ChangeListener<GameEvent.LevelResetEvent>(new LevelResetListener(), true);
        }

        private static void ClearLines() {
            if (!_linesReady()) return;
            foreach (LineInfo li in _lines) {
                li.queue.Clear();
                li.renderer.positionCount = li.queue.Count;
                li.renderer.SetPositions(li.queue.ToArray());
            }
        }

        private static IEnumerable<PlayerLine> GetPlayers() {
            if (LobbyManager.instance == null) yield break;
            if (LobbyManager.instance.CurrentGameController != null) {
                Dictionary<int, GamePlayer> players = LobbyManager.instance.CurrentGameController.CurrentPlayerQueue.ToDictionary(gp => gp.networkNumber - 1);
                for (int i = 0; i < _lines.Length; i++) {
                    GamePlayer gp;
                    if (!players.TryGetValue(i, out gp) || gp.CharacterInstance == null) {
                        _lines[i].queue.Clear();
                        _lines[i].renderer.positionCount = _lines[i].queue.Count;
                        _lines[i].renderer.SetPositions(_lines[i].queue.ToArray());
                        continue;
                    }
                    _lines[i].renderer.startColor = gp.PlayerColor;
                    _lines[i].renderer.endColor = gp.PlayerColor;
                    yield return new PlayerLine { character = gp.CharacterInstance, line = _lines[i] };
                }
            } else {
                Dictionary<int, LobbyPlayer> players = LobbyManager.instance.GetLobbyPlayers().ToDictionary(lp => lp.networkNumber - 1);
                for (int i = 0; i < _lines.Length; i++) {
                    LobbyPlayer lp;
                    if (!players.TryGetValue(i, out lp) || lp.CharacterInstance == null) {
                        _lines[i].queue.Clear();
                        _lines[i].renderer.positionCount = _lines[i].queue.Count;
                        _lines[i].renderer.SetPositions(_lines[i].queue.ToArray());
                        continue;
                    }
                    _lines[i].renderer.startColor = lp.PlayerColor;
                    _lines[i].renderer.endColor = lp.PlayerColor;
                    yield return new PlayerLine { character = lp.CharacterInstance, line = _lines[i] };
                }
            }
        }
    }
}
