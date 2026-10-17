// 玩家移动轨迹：在玩家身后画一条轨迹线（移植自 UCHPlayerTrackerMod）。
// 自行创建常驻 GameObject + TrackerComponent 驱动，不走核心的页面/Register 机制。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;

namespace SR_UCH.Tweaks {
    // 玩家移动轨迹线：在每位玩家身后绘制一条轨迹。
    [SrFeature("sr.playertracker", "PlayerTracker", "1.0")]
    public class PlayerTracker : ITweak {
        private static IFeatureHost _mp;   // 只用来读 Config（契约类型）
        private static ConfigEntry<int> _trackingLength;
        private static ConfigEntry<int> _skipFrames;
        private static ConfigEntry<float> _lineWidthStart;
        private static ConfigEntry<float> _lineWidthEnd;
        private static ConfigEntry<float> _teleportThreshold;
        private static ConfigEntry<float> _alpha;      // 轨迹透明度
        // 运行期开关：游戏内管理器同时也可改。
        public static bool Enabled = true;

        // 透明度取值辅助（异常/未绑定 -> 1）。
        public static float Alpha() {
            try { return _alpha != null ? Mathf.Clamp(_alpha.Value, 0.05f, 1f) : 1f; }
            catch { return 1f; }
        }
        // 以下取值一律 Clamp：AcceptableValueRange 只约束 UI，手改的 cfg 仍会返回超范围值；
        // 负数线宽会让 LineRenderer 异常，负轨迹长度会让队列被立刻丢空。
        public static int TrackingLength() {
            try { return _trackingLength != null ? Mathf.Clamp(_trackingLength.Value, 10, 3000) : 120; }
            catch { return 120; }
        }
        public static int SkipFrames() {
            try { return _skipFrames != null ? Mathf.Clamp(_skipFrames.Value, 0, 60) : 0; }
            catch { return 0; }
        }
        public static float TeleportThreshold() {
            try { return _teleportThreshold != null ? Mathf.Clamp(_teleportThreshold.Value, 1f, 500f) : 10f; }
            catch { return 10f; }
        }
        public static float LineWidthStart() {
            try { return _lineWidthStart != null ? Mathf.Clamp(_lineWidthStart.Value, 0.01f, 2f) : 0.1f; }
            catch { return 0.1f; }
        }
        public static float LineWidthEnd() {
            try { return _lineWidthEnd != null ? Mathf.Clamp(_lineWidthEnd.Value, 0.01f, 2f) : 0.1f; }
            catch { return 0.1f; }
        }

        private class LineInfo {
            public Queue<Vector3> queue = new Queue<Vector3>();
            public LineRenderer renderer;
            public GameObject go;
            // 最近一个记录点：Queue<T> 的 Last() 是 O(n) 遍历，入队时顺手记住。
            public Vector3 lastPos;
            public bool hasLastPos;
        }

        private struct PlayerLine {
            public Character character;
            public LineInfo line;
        }

        private static LineInfo[] _lines;
        private static GameObject _trackerRoot;
        private static readonly Dictionary<int, GamePlayer> _gpByIndex = new Dictionary<int, GamePlayer>();
        private static readonly Dictionary<int, LobbyPlayer> _lpByIndex = new Dictionary<int, LobbyPlayer>();

        private class LevelResetListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                // 事件回调抛异常会冒到 GameEventManager 并影响同链其它监听器，故只记日志。
                try {
                    ClearLines();
                } catch (Exception __ex) { SRApi.Guard.Log("移动轨迹：关卡重置清理", __ex); }
            }
        }

        private class TrackerComponent : MonoBehaviour {
            public int framesLeft;
            private bool _lastEff;
            private void FixedUpdate() {
                if (!_linesReady()) return;
                bool eff = Enabled && SRApi.GateMaster;
                // 只在显示状态变化时 SetActive。
                if (eff != _lastEff) {
                    _lastEff = eff;
                    foreach (LineInfo li in _lines) {
                        if (li == null) continue;   // 着色器缺失时槽位为 null
                        if (li.go != null && li.go.activeSelf != eff) li.go.SetActive(eff);
                    }
                }
                if (!eff) return;
                if (framesLeft > 0) { framesLeft--; return; }
                framesLeft = SkipFrames();
                // 每个玩家各自一个 try：出错只跳过该玩家（下帧重试），并经 SRApi.Guard.Log 去重记录。
                foreach (PlayerLine pl in GetPlayers()) {
                  try {
                        Vector3 pos = pl.character.transform.position;
                        // 异常坐标过滤：与上一记录点距离超过阈值（瞬移/角色消失）则清空重来，避免拖出贯穿全图的长线。
                        float threshold = TeleportThreshold();
                        if (pl.line.hasLastPos) {
                            float d = Vector3.Distance(pl.line.lastPos, pos);
                            if (d > threshold) {
                                // 只置 positionCount = 0，不重新分配数组
                                pl.line.queue.Clear();
                                pl.line.hasLastPos = false;
                                pl.line.renderer.positionCount = 0;
                                continue;
                            }
                        }
                        pl.line.queue.Enqueue(pos);
                        pl.line.lastPos = pos;
                        pl.line.hasLastPos = true;
                        while (pl.line.queue.Count > TrackingLength()) pl.line.queue.Dequeue();
                        // 逐点 SetPosition 写入，避免每帧 queue.ToArray() 分配。
                        ApplyPositions(pl.line);
                  } catch (Exception e) {
                        // PlayerLine 是结构体，不能与 null 比较，只判 character
                        SRApi.Guard.Log("轨迹更新 " + (pl.character != null ? pl.character.name : "?"), e);
                        try { ClearLine(pl.line); } catch { }   // 出错的玩家本帧不画，下帧重试
                  }
                }
            }
        }

        private static bool _linesReady() { return _lines != null; }

        private static void SelfReg() {
            SR.LocSec("Player Tracker", "移动轨迹", null);
            SRApi.Nav("Player Tracker", 20); // 侧栏顺序 20
            // 每个栏目自己声明自己的页面：缺了它只会靠核心兜底分支成页，外部模块无法替换这一页。
            SR.RegisterPage("Player Tracker", Render);
            SR.LocKey("Player Tracker", "Tracking Length", "轨迹长度", null);
            SR.LocDesc("Player Tracker", "Tracking Length", "记录多长时间的移动轨迹, 单位是格, 60 格大概一秒.", "How long of a movement trail to record (60 ticks ≈ 1 second)");
            SR.LocKey("Player Tracker", "Skip Frames", "跳帧数", null);
            SR.LocDesc("Player Tracker", "Skip Frames", "每跳过几帧才记一个点, 数字越大轨迹越稀.", "Record a point every N frames (higher = sparser trail)");
            SR.LocKey("Player Tracker", "Line Start Width", "起点宽度", null);
            SR.LocDesc("Player Tracker", "Line Start Width", "轨迹靠近当前位置那一头有多粗.", "Trail start width (near the current position)");
            SR.LocKey("Player Tracker", "Line End Width", "终点宽度", null);
            SR.LocDesc("Player Tracker", "Line End Width", "轨迹最老那一头有多粗.", "Trail end width (at the oldest point)");
            SR.LocKey("Player Tracker", "Teleport Threshold", "瞬移清除距离", null);
            SR.LocDesc("Player Tracker", "Teleport Threshold", "一帧里移动超过这个距离就算瞬移, 把画好的线擦掉重画. 不然会拖出一条穿全图的直线. 最小 1.", "Teleport clear distance (units): if a character moves farther than this in one frame (teleport/warp/party-box pick), delete the drawn trail and restart, so no giant line spans the map. Min 1.");
            SR.LocKey("Player Tracker", "Enabled", "移动轨迹总开关", null);
            SR.LocDesc("Player Tracker", "Enabled", "在画面上把其他玩家走过的轨迹画出来.", "Master switch: draw movement trails for other players on screen");
        }

        // 本页内容 = 该段的条目列表。
        private static void Render() {
            try {
                List<ConfigEntryBase> list = SR.SectionEntries("Player Tracker");
                if (list == null || list.Count == 0) { GUILayout.Label(SR.T("（无匹配条目）", "(no matching entries)"), SR.Ctl.Label); return; }
                SR.RenderSectionEntries("Player Tracker");
            } catch (Exception __ex) { SRApi.Guard.Log("渲染移动轨迹页", __ex); }
        }

        // 初始化需整段 try：中途抛异常会留下 DontDestroyOnLoad 的常驻对象，且功能静默消失。
        public void Initialize(IFeatureHost plugin) {
          try {
            SelfReg();
            _mp = plugin;
            ConfigEntry<bool> enabled = _mp.Config.Bind("Player Tracker", "Enabled", false, "总开关");
            Enabled = enabled.Value;
            enabled.SettingChanged += (s, e) => Enabled = enabled.Value;
            _trackingLength = _mp.Config.Bind(
                "Player Tracker",
                "Tracking Length",
                120,
                new ConfigDescription("轨迹长度：记录多长时间的移动轨迹（10-3000 格，60 格 ≈ 1 秒；默认 120）",
                    new AcceptableValueRange<int>(10, 3000)));
            _skipFrames = _mp.Config.Bind(
                "Player Tracker",
                "Skip Frames",
                0,
                new ConfigDescription("跳帧数：每跳过 N 帧才记录一个位置点（0-60，越大轨迹越稀疏；默认 0）",
                    new AcceptableValueRange<int>(0, 60)));
            _lineWidthStart = _mp.Config.Bind(
                "Player Tracker",
                "Line Start Width",
                0.1f,
                new ConfigDescription("起点宽度（0.01-2，越靠近当前位置越宽/越细）", new AcceptableValueRange<float>(0.01f, 2f)));
            _lineWidthEnd = _mp.Config.Bind(
                "Player Tracker",
                "Line End Width",
                0.1f,
                new ConfigDescription("终点宽度（0.01-2，最旧位置点的宽度）", new AcceptableValueRange<float>(0.01f, 2f)));
            _teleportThreshold = _mp.Config.Bind(
                "Player Tracker",
                "Teleport Threshold",
                10f,
                new ConfigDescription("瞬移判定距离（1-500）：角色瞬移超过这个距离就清空轨迹重来，避免拖出贯穿全图的长线。默认 10",
                    new AcceptableValueRange<float>(1f, 500f)));
            _alpha = _mp.Config.Bind(
                "Player Tracker",
                "Trail Alpha",
                1f,
                new ConfigDescription("轨迹透明度（0.05-1，默认 1 = 完全不透明）。调低可让轨迹不遮挡画面。",
                    new AcceptableValueRange<float>(0.05f, 1f)));
            SR.LocKey("Player Tracker", "Trail Alpha", "轨迹透明度", "Trail opacity");
            SR.LocDesc("Player Tracker", "Trail Alpha",
                "轨迹有多不透明, 0.05 到 1, 默认 1 是完全不透明. 调低就不挡画面.",
                "Trail opacity (1 = fully opaque).");
            // 宽度只在 CreateLine 里读一次，配置变更时需刷新已存在的线。
            _lineWidthStart.SettingChanged += (s, e) => ApplyWidths();
            _lineWidthEnd.SettingChanged += (s, e) => ApplyWidths();
            // 透明度逐帧写进 renderer，无需订阅配置变更。

            // 常驻对象：承载逐帧更新与所有轨迹线
            GameObject go = new GameObject("SR_UCHPlayerTracker");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TrackerComponent>();

            _trackerRoot = go;
            // 行池按需增长，支持超过 8 人。
            EnsureCapacity(8);

            GameEventManager.ChangeListener<GameEvent.LevelResetEvent>(new LevelResetListener(), true);
          } catch (Exception __ex) { SRApi.Guard.Log("移动轨迹 初始化", __ex); }
        }

        private static void ClearLines() {
            if (!_linesReady()) return;
            foreach (LineInfo li in _lines) ClearLine(li);
        }

        // 把队列里的点写进 LineRenderer：逐点 SetPosition，避免每帧分配。
        private static void ApplyPositions(LineInfo line) {
            int n = line.queue.Count;
            line.renderer.positionCount = n;
            if (n == 0) return;
            int i = 0;
            foreach (Vector3 v in line.queue) line.renderer.SetPosition(i++, v);
        }

        // 把当前宽度设置应用到已创建的线（配置变更时调用）。
        private static void ApplyWidths() {
            if (_lines == null) return;
            foreach (LineInfo li in _lines) {
                if (li == null || li.renderer == null) continue;
                li.renderer.startWidth = LineWidthEnd();
                li.renderer.endWidth = LineWidthStart();
            }
        }

        private static void ClearLine(LineInfo line) {
            if (line == null) return;
            line.queue.Clear();
            line.hasLastPos = false;
            if (line.renderer != null) line.renderer.positionCount = 0;
        }

        private static void EnsureCapacity(int count) {
            if (_lines != null && _lines.Length >= count) return;
            int target = Mathf.Max(count, _lines == null ? 8 : _lines.Length);
            LineInfo[] arr = new LineInfo[target];
            int old = _lines == null ? 0 : _lines.Length;
            if (old > 0) Array.Copy(_lines, arr, old);
            for (int i = old; i < target; i++) {
                // 着色器缺失（已告警）时不再重试，槽位保持 null；消费点均已判空。
                if (_shaderMissingWarned) { arr[i] = null; continue; }
                arr[i] = CreateLine(i);
            }
            _lines = arr;
        }

        // 所有轨迹线共用一个材质，避免逐条创建导致泄漏。
        private static Material _lineMat;

        private static bool _shaderMissingWarned;

        private static LineInfo CreateLine(int i) {
            // 先取材质再建对象：Shader.Find 在裁剪过的构建里可能返回 null，new Material(null) 会抛异常，
            // 若发生在建对象之后会留下永久残留的 DontDestroyOnLoad 对象。逐级回退，全失败则返回 null 并只告警一次。
            if (_lineMat == null) {
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("UI/Default");
                if (sh == null) sh = Shader.Find("Unlit/Transparent");
                if (sh == null) {
                    if (!_shaderMissingWarned) {
                        _shaderMissingWarned = true;
                        SRApi.LogWarn("[移动轨迹] 找不到可用着色器（Sprites/Default、UI/Default、Unlit/Transparent 都没有），移动轨迹将不显示");
                    }
                    return null;
                }
                _lineMat = new Material(sh);
            }
            LineInfo li = new LineInfo();
            li.go = new GameObject("TrackerLine" + i);
            if (_trackerRoot != null) li.go.transform.SetParent(_trackerRoot.transform);
            LineRenderer lr = li.go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _lineMat;
            lr.startWidth = LineWidthEnd();
            lr.endWidth = LineWidthStart();
            lr.useWorldSpace = true;
            li.renderer = lr;
            return li;
        }

        private static IEnumerable<PlayerLine> GetPlayers() {
            LobbyManager lm = LobbyManager.instance;
            if (lm == null) yield break;
            if (lm.CurrentGameController != null) {
                // 复用字典避免每帧分配；行池按最大玩家索引增长。
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
                    if (_lines[i] == null) continue;   // 着色器缺失时槽位为 null
                    GamePlayer gp;
                    if (!_gpByIndex.TryGetValue(i, out gp) || gp == null || gp.CharacterInstance == null) { ClearLine(_lines[i]); continue; }
                    Color c = gp.PlayerColor; c.a *= Alpha();
                    _lines[i].renderer.startColor = c;
                    _lines[i].renderer.endColor = c;
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
                    if (_lines[i] == null) continue;   // 着色器缺失时槽位为 null
                    LobbyPlayer lp;
                    if (!_lpByIndex.TryGetValue(i, out lp) || lp == null || lp.CharacterInstance == null) { ClearLine(_lines[i]); continue; }
                    Color c2 = lp.PlayerColor; c2.a *= Alpha();
                    _lines[i].renderer.startColor = c2;
                    _lines[i].renderer.endColor = c2;
                    yield return new PlayerLine { character = lp.CharacterInstance, line = _lines[i] };
                }
            }
        }
    }
}
