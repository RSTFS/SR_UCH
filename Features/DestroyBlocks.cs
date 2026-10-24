// 方块破坏：按玩家收集放置历史，按住选择键显示名牌标签，按 1-9 回滚该玩家最近的放置。
// 机制照参考实现 RemovePlayerPlacements：只用 GameEvent 监听收集 + 每帧按键，不注册任何 Harmony 补丁。
// 房主端才有意义（回滚要发网络信号，只有房主权威）；房客只提示“仅房主可用”。
// 槽位：对外 DestroyBlocks.IsPlayerPlaced / PlayerPlacedBlocks / AttributeToHost，供 EX 与关卡快照经 Slots 取用。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.destroyblocks", "DestroyBlocks", "2.0")]
    public class DestroyBlocks : ITweak {
        private const string Sec = "Destroy Blocks";
        private const string KeySelection = "Selection Key"; // 选择键（按住进入选择态）
        private const string KeyUseSmoke = "Use Smoke";      // 回滚时是否播烟雾动画

        private static IFeatureHost _mp; // 仅用于读 Config（契约类型）
        private static ConfigEntry<KeyCode> _selectKey;
        private static ConfigEntry<bool> _useSmoke;

        // 放置历史：玩家号 -> 该玩家的放置序列（索引越大越新，回滚从末尾往前按 offsetIndex 取）。
        private static readonly Dictionary<int, List<Placement>> _history = new Dictionary<int, List<Placement>>();

        // 一条放置记录：对象本身、ID、原始位置/朝向、是否已销毁、放置者信息。
        public class Placement {
            public Placeable placedObject;
            public int id;
            public Vector2 originalPosition;
            public Quaternion originalRotation;
            public bool destroyed;
            public PlayerRef playerRef;
        }

        // 放置者快照：名字取 LobbyManager，颜色取 LobbyPlayer.NetworkPlayerColor。
        public class PlayerRef {
            public string name;
            public int playerNumber;
            public Color color;
        }

        // 一个名牌标签：tagObj 是克隆出来的标签，targetObj 是它跟随的方块。
        private class HighlightEntry {
            public GameObject tagObj;
            public GameObject targetObj;
        }

        private static readonly List<HighlightEntry> _tags = new List<HighlightEntry>();
        private static Character[] _characters;     // 每帧角色缓存，按下选择键时重扫
        private static GameObject _nameTagPrefab;    // 从角色光标名牌克隆出来的 prefab 缓存
        private static int _offsetIndex;             // 往回第几个放置（0 = 最新）
        private static int _lastOffsetIndex;         // 上次滚轮后的下标，仅用于变化判定
        private static bool _indicating;             // 当前是否已显示指示标签

        // 临时道具（炸弹，放置后自爆）不能进历史：回滚它们等于无意义的重播。
        // 名字与参考实现逐字一致；PlayerNumber <= 0 表示非玩家放置（系统 / 关卡自带件），同样忽略。
        private static readonly string[] _tmpPieces = new string[] { "Bomb Mini", "3x3 Bomb", "Bomb Mega" };

        // 回滚是否播放烟雾动画（未绑定时按关闭处理）。
        private static bool UseSmoke { get { return _useSmoke != null && _useSmoke.Value; } }

        // 房主判定：参考实现用 LobbyManager.instance.IsHost（离线 StartHost 时也为 true）。
        private static bool IsHostNow() {
            try {
                LobbyManager lm = LobbyManager.instance;
                return lm != null && lm.IsHost;
            } catch { return false; }
        }

        // ---------------------------- 历史收集 ----------------------------

        // 取放置者信息：名字 / 颜色都来自 LobbyManager，取不到时用中性占位，颜色兜底为白色。
        private static PlayerRef MakePlayerRef(int playerNumber) {
            PlayerRef pref = new PlayerRef();
            pref.playerNumber = playerNumber;
            pref.name = "Player " + playerNumber;
            pref.color = Color.white;
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    LobbyPlayer lp = lm.GetLobbyPlayer(playerNumber);
                    if (lp != null) {
                        if (!string.IsNullOrEmpty(lp.playerName)) pref.name = lp.playerName;
                        // Color 默认是 (0,0,0,0)：漏填 alpha 会让名牌文字全透明，故显式兜底。
                        pref.color = lp.NetworkPlayerColor;
                        if (pref.color.a <= 0.01f) pref.color.a = 1f;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.玩家信息", __ex); }
            return pref;
        }

        // 记一条放置：忽略炸弹等临时件与玩家号 <= 0 的系统件。
        private static void AddPlacement(int playerNumber, Placeable placedObject) {
            try {
                if (placedObject == null || playerNumber <= 0) return;
                string name = placedObject.Name;
                if (!string.IsNullOrEmpty(name)) {
                    for (int i = 0; i < _tmpPieces.Length; i++) {
                        if (name == _tmpPieces[i]) return;
                    }
                }
                Placement pl = new Placement();
                pl.placedObject = placedObject;
                pl.id = placedObject.ID;
                pl.originalPosition = placedObject.OriginalPosition;
                pl.originalRotation = placedObject.OriginalRotation;
                pl.destroyed = false;
                pl.playerRef = MakePlayerRef(playerNumber);
                List<Placement> list;
                if (!_history.TryGetValue(playerNumber, out list) || list == null) {
                    list = new List<Placement>();
                    _history[playerNumber] = list;
                }
                list.Add(pl);
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.记录放置", __ex); }
        }

        // 从所有玩家的历史里摘掉某对象（销毁事件 / 已失效对象）。
        private static void RemoveFromHistory(Placeable p) {
            if (p == null) return;
            try {
                foreach (KeyValuePair<int, List<Placement>> kv in _history) {
                    List<Placement> list = kv.Value;
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--) {
                        Placement pl = list[i];
                        if (pl != null && pl.placedObject == p) list.RemoveAt(i);
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.移除记录", __ex); }
        }

        // 某对象的记录（子元件沿 ParentPiece 上溯，与旧实现的归属解析一致）。
        private static Placement FindHistoryOf(Placeable p) {
            if (p == null) return null;
            Placement pl = FindDirect(p);
            if (pl != null) return pl;
            try {
                Placeable cur = p;
                for (int i = 0; i < 8; i++) {
                    Placeable up = cur.ParentPiece;
                    if (up == null || up == cur) break;
                    pl = FindDirect(up);
                    if (pl != null) return pl;
                    cur = up;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.解析子元件归属", __ex); }
            return null;
        }

        private static Placement FindDirect(Placeable p) {
            if (p == null) return null;
            foreach (KeyValuePair<int, List<Placement>> kv in _history) {
                List<Placement> list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++) {
                    Placement pl = list[i];
                    if (pl != null && pl.placedObject == p) return pl;
                }
            }
            return null;
        }

        private static void ClearHistory() {
            _history.Clear();
        }

        // 所有历史里最长的一条（滚轮下标的钳制上限）。
        private static int MaxHistory() {
            int max = 0;
            foreach (KeyValuePair<int, List<Placement>> kv in _history) {
                List<Placement> list = kv.Value;
                if (list != null && list.Count > max) max = list.Count;
            }
            return max;
        }

        private static void ClampOffset() {
            _offsetIndex = Mathf.Clamp(_offsetIndex, 0, Mathf.Max(0, MaxHistory() - 1));
            _lastOffsetIndex = _offsetIndex;
        }

        // ---------------------------- 事件监听 ----------------------------

        // 参考实现用三个独立监听器，这里合并成一个：三张表都登记同一对象，语义一致。
        // 事件回调抛异常会冒到 GameEventManager 日志并影响同链其它监听器，故整段兜住。
        public class Collector : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    // 总开关关闭时不收集：关掉功能后历史不再增长，也不会残留跨开关的旧记录。
                    if (!SRApi.GateMaster) return;
                    PiecePlacedEvent placed = e as PiecePlacedEvent;
                    if (placed != null) {
                        if (placed.PlayerNumber == 0) return;
                        AddPlacement(placed.PlayerNumber, placed.PlacedBlock);
                        return;
                    }
                    DestroyPieceEvent destroyed = e as DestroyPieceEvent;
                    if (destroyed != null) {
                        RemoveFromHistory(destroyed.Piece);
                        return;
                    }
                    StartPhaseEvent phase = e as StartPhaseEvent;
                    if (phase != null && phase.Phase == GameControl.GamePhase.START) {
                        // 新回合：上一回合的放置已随关卡重置消失，历史与指示标签一并作废。
                        ClearHistory();
                        HideIndicator();
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.事件", __ex); }
            }
        }

        private static void OnSceneChanged(Scene a, Scene b) {
            ClearHistory();
            // 角色缓存与名牌 prefab 都指向旧场景对象，必须一起清。
            _characters = null;
            _nameTagPrefab = null;
            _offsetIndex = 0;
            _lastOffsetIndex = 0;
            HideIndicator();
        }

        // ---------------------------- 交互 ----------------------------

        // 每帧入口（SRApi.RegisterTick）：先判总开关 / 面板 / 模式，再处理按住、数字键与滚轮。
        private static void Tick() {
          try {
            if (_selectKey == null) return;
            if (!SRApi.GateMaster) { if (_indicating) HideIndicator(); return; }
            // 面板打开时不抢输入：选择键默认 Alt，开着界面按它会干扰键位录制。
            if (SRApi.UiOpen) { if (_indicating) HideIndicator(); return; }
            // 仅派对 / 创意 / 自由建造模式（与旧实现的模式门一致）；不满足时收起标签，免得停在画面上。
            if (!SRApi.GateModeAllows(SRApi.ModeMask.Party | SRApi.ModeMask.Creative | SRApi.ModeMask.Freeplay)) {
                if (_indicating) HideIndicator();
                return;
            }

            bool down = SRApi.ComboKeyDown(_selectKey);
            bool held = SRApi.ComboKeyHeld(_selectKey);
            if (down) {
                _offsetIndex = 0;
                _lastOffsetIndex = 0;
                _characters = null; // 每次按下重扫角色，别长期持有已销毁的对象
                if (!IsHostNow()) { SRApi.NotifyT("仅房主可用", "Host only"); return; }
            }
            if (!held) {
                if (_indicating) HideIndicator();
                return;
            }
            if (!IsHostNow()) {
                if (_indicating) HideIndicator(); // 房客不该留指示标签
                return;
            }
            if (_characters == null) _characters = UnityEngine.Object.FindObjectsOfType<Character>();
            ShowIndicator();
            // 数字键 1..9（主键盘与小键盘）：回滚对应玩家偏移后的那个放置。
            for (int i = 1; i <= 9; i++) {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + (i - 1))) ||
                    Input.GetKeyDown((KeyCode)((int)KeyCode.Keypad1 + (i - 1)))) {
                    Rollback(i);
                    break;
                }
            }
            Scroll();
            UpdateHighlights();
          } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.按键", __ex); }
        }

        // 滚轮：offsetIndex 在 [0, max(count)-1] 内加减，切换“回滚第几个最近的放置”。
        private static void Scroll() {
            // 面板未绘制的那一帧核心拿不到滚轮事件，故再退回读输入轴兜底（老 Unity 路径仍可能为 0）。
            float wheel = SRApi.TakeWheelEvent();
            if (Mathf.Abs(wheel) < 0.0001f) {
                try { wheel = Input.GetAxis("Mouse ScrollWheel"); } catch { wheel = 0f; }
                if (Mathf.Abs(wheel) > 0.0001f) SRApi.MarkWheelUsed(); // 滚轮已被选择器吃掉，相机让路
            }
            if (Mathf.Abs(wheel) < 0.0001f) return;
            int step = wheel < 0f ? 1 : -1; // 向下滚 = 往回更早的放置
            _offsetIndex = Mathf.Clamp(_offsetIndex + step, 0, Mathf.Max(0, MaxHistory() - 1));
            if (_offsetIndex != _lastOffsetIndex) {
                // 偏移变了：标签要跟着换到另一批方块上。
                HideIndicator();
                ShowIndicator();
            }
            _lastOffsetIndex = _offsetIndex;
        }

        // 取某玩家偏移后的放置；列表空 / 无该玩家时返回 null。
        private static Placement GetPlacementWithOffsetBy(int playerNumber) {
            List<Placement> list;
            if (!_history.TryGetValue(playerNumber, out list) || list == null || list.Count == 0) return null;
            int index = Mathf.Clamp(list.Count - 1 - _offsetIndex, 0, list.Count - 1);
            return list[index];
        }

        // 回滚：把该玩家偏移后的那条放置销毁（发网络信号，全员同步）。
        private static void Rollback(int playerNumber) {
            try {
                Placement pl = GetPlacementWithOffsetBy(playerNumber);
                if (pl == null) return;
                if (pl.destroyed) return;
                List<Placement> list;
                if (!_history.TryGetValue(playerNumber, out list) || list == null) return;
                Placeable obj = pl.placedObject;
                if (obj == null || obj.gameObject == null) {
                    // 已被游戏自己回收（掉出界 / 被炸）：只摘记录，不销毁。
                    list.Remove(pl);
                    ClampOffset();
                    return;
                }
                list.Remove(pl);
                ClampOffset();
                // 三个 bool：销毁子件 / 播烟雾动画 / 发网络信号。DestroySelf 自身会发 MsgPieceDestroyed。
                obj.DestroySelf(false, UseSmoke, true);
                HideIndicator();
                ShowIndicator();
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.回滚", __ex); }
        }

        // ---------------------------- 指示标签 ----------------------------

        private static HighlightEntry FindTag(GameObject target) {
            for (int i = 0; i < _tags.Count; i++) {
                HighlightEntry h = _tags[i];
                if (h != null && h.targetObj == target) return h;
            }
            return null;
        }

        // 为每位玩家偏移后的放置克隆一个名牌：文本 “(玩家号) 玩家名”，颜色取该玩家颜色。
        private static void ShowIndicator() {
            if (_indicating) return;
            try {
                foreach (KeyValuePair<int, List<Placement>> kv in _history) {
                    List<Placement> list = kv.Value;
                    if (list == null || list.Count == 0) continue;
                    int index = Mathf.Clamp(list.Count - 1 - _offsetIndex, 0, list.Count - 1);
                    Placement pl = list[index];
                    if (pl == null || pl.destroyed) continue;
                    Placeable placed = pl.placedObject;
                    if (placed == null) continue;
                    GameObject target = placed.gameObject;
                    if (target == null) continue;
                    if (FindTag(target) != null) continue; // 同一方块已经有标签，不重复克隆
                    string hint = "(" + kv.Key + ") " + (pl.playerRef != null ? pl.playerRef.name : "");
                    Color color = pl.playerRef != null ? pl.playerRef.color : Color.white;
                    Highlight(target, hint, color);
                }
                _indicating = true;
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.显示指示", __ex); }
        }

        // 克隆一个名牌并把它放到目标方块位置（父级置空，避免复制游戏 UI 层级导致布局错乱）。
        private static void Highlight(GameObject target, string hint, Color color) {
            try {
                if (target == null) return;
                GameObject tag = GetCopiedNameTag();
                if (tag == null) return;
                tag.transform.SetParent(null);
                tag.transform.position = target.transform.position;
                Text text = tag.GetComponentInChildren<Text>();
                if (text != null) {
                    text.text = hint;
                    text.color = color;
                }
                HighlightEntry entry = new HighlightEntry();
                entry.tagObj = tag;
                entry.targetObj = target;
                _tags.Add(entry);
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.克隆名牌", __ex); }
        }

        // 每帧跟随：目标被销毁就销毁标签，否则把标签挪到目标位置。
        private static void UpdateHighlights() {
            if (!_indicating) return;
            try {
                for (int i = _tags.Count - 1; i >= 0; i--) {
                    HighlightEntry h = _tags[i];
                    if (h == null || h.targetObj == null) {
                        if (h != null && h.tagObj != null) UnityEngine.Object.Destroy(h.tagObj);
                        _tags.RemoveAt(i);
                    } else if (h.tagObj != null) {
                        h.tagObj.transform.position = h.targetObj.transform.position;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.更新指示", __ex); }
        }

        private static void HideIndicator() {
            if (!_indicating) return;
            try {
                for (int i = 0; i < _tags.Count; i++) {
                    if (_tags[i] != null && _tags[i].tagObj != null) UnityEngine.Object.Destroy(_tags[i].tagObj);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.隐藏指示", __ex); }
            _tags.Clear();
            _indicating = false;
        }

        // 从任一角色光标的 nameTag 缓存 prefab，再克隆；缓存失效（切场景）后重新扫描。
        private static GameObject GetCopiedNameTag() {
            if (_nameTagPrefab == null) {
                if (_characters == null) return null;
                for (int i = 0; i < _characters.Length; i++) {
                    Character ch = _characters[i];
                    if (ch == null || ch.AssociatedGamePlayer == null) continue;
                    var cursor = ch.AssociatedGamePlayer.CursorInstance;
                    if (cursor == null || cursor.nameTag == null) continue;
                    _nameTagPrefab = cursor.nameTag.gameObject;
                    break;
                }
            }
            if (_nameTagPrefab == null) return null;
            GameObject go = UnityEngine.Object.Instantiate(_nameTagPrefab);
            if (go == null) return null;
            NameTag nt = go.GetComponent<NameTag>();
            if (nt != null) {
                // 克隆体的名牌默认是给光标用的，这里强制全不透明并压到最上层。
                nt.currentAlpha = 1f;
                if (nt.nameCanvasGroup != null) nt.nameCanvasGroup.alpha = 1f;
                if (nt.canvas != null) nt.canvas.sortingOrder = 32767;
            }
            return go;
        }

        // ---------------------------- 对外槽位 ----------------------------

        // 供外部模块（EX 清除地图对象 / 无视对象）调用：是否为玩家放置的道具。
        public static bool IsPlayerPlaced(Placeable p) {
            try { return FindHistoryOf(p) != null; } catch { return false; }
        }

        // 玩家放置的方块清单（不销毁），供清场功能使用。
        public static List<Placeable> PlayerPlacedBlocks() {
            List<Placeable> result = new List<Placeable>();
            try {
                foreach (KeyValuePair<int, List<Placement>> kv in _history) {
                    List<Placement> list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++) {
                        Placeable obj = list[i] != null ? list[i].placedObject : null;
                        // 游戏自己销毁方块时不派发 DestroyPieceEvent，这里顺手过滤死对象。
                        if (obj == null || obj.MarkedForDestruction) continue;
                        if (result.Contains(obj)) continue;
                        result.Add(obj);
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.玩家放置清单", __ex); }
            return result;
        }

        // 把快照加载出来的方块登记为房主（1 号）放置；由关卡功能经 DestroyBlocks.AttributeToHost 槽位调用。
        public static void AttributeToHost(List<Placeable> blocks) {
            try {
                if (blocks == null || blocks.Count == 0) return;
                for (int i = 0; i < blocks.Count; i++) {
                    Placeable p = blocks[i];
                    if (p == null) continue;
                    if (FindHistoryOf(p) != null) continue; // 已有归属的不重复登记
                    AddPlacement(1, p);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.归属房主", __ex); }
        }

        // 槽位名：与调用方的唯一约定是“名字 + 委托签名”，刻意用 private const，调用方另写自己的常量。
        private const string SlotIsPlayerPlaced = "DestroyBlocks.IsPlayerPlaced";        // Func<Placeable,bool>
        private const string SlotPlayerPlacedBlocks = "DestroyBlocks.PlayerPlacedBlocks"; // Func<List<Placeable>>
        private const string SlotAttributeToHost = "DestroyBlocks.AttributeToHost";      // Action<List<Placeable>>

        // ---------------------------- 页面 ----------------------------

        private static void RenderPage() {
            try {
                SRApi.Ctl.SectionTitle("- " + SRApi.T("选择与回滚", "Selection and rollback") + " -",
                    SRApi.T("方块破坏：按住选择键后，场上每位玩家最近放置的方块上会出现“(玩家号) 玩家名”标签。\n按住期间按数字键 1-9 回滚对应玩家的放置，滚轮切换要回滚第几个最近的放置。\n仅房主可用，回滚会通过网络信号同步给所有玩家。",
                        "Block destruction: while the selection key is held, each player's latest placement is labelled with \"(player number) name\".\nPress 1-9 while holding to roll back that player's placement; the wheel picks which recent placement.\nHost only; rollbacks are synced to every player through the network."));

                ConfigEntryBase keyEntry = SRApi.Ctl.FindEntry(Sec, KeySelection);
                GUILayout.BeginHorizontal();
                SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("选择键", "Selection key"),
                    SRApi.T("选择键\n按住显示放置标签，按 1-9 回滚，滚轮切换层级\n仅房主可用；点键框后按新键改绑。",
                        "selection key\nhold to show placement labels, press 1-9 to roll back, wheel changes the level\nhost only; click the key box and press a new key to rebind")),
                    keyEntry, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(52));
                if (keyEntry != null) SRApi.Ctl.RenderControl(keyEntry);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(SRApi.Ctl.Sc(2));

                ConfigEntryBase smokeEntry = SRApi.Ctl.FindEntry(Sec, KeyUseSmoke);
                GUILayout.BeginHorizontal();
                SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("使用烟雾动画", "Use smoke animation"),
                    SRApi.T("使用烟雾动画\n回滚销毁方块时播放游戏原生的烟雾特效\n默认关闭；只影响画面表现。",
                        "smoke animation\nplays the game's native smoke effect when a rolled-back block is destroyed\noff by default; visual only")),
                    smokeEntry, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(52));
                if (smokeEntry != null) SRApi.Ctl.RenderControl(smokeEntry);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                SRApi.Ctl.SectionTitle("- " + SRApi.T("操作方式", "How to use") + " -",
                    SRApi.T("操作步骤\n按住选择键 → 看标签确认玩家号 → 按 1-9 回滚\n标签只在本机显示，回滚结果对全员生效。",
                        "Steps\nhold the selection key, read the label to confirm the player number, then press 1-9 to roll back\nlabels are local only; the rollback itself applies to everyone"));
                float width = Mathf.Max(SRApi.Ctl.Sc(160), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(36));
                string steps = SRApi.T(
                    "1. 按住「选择键」，场上每位玩家最新放置的方块上出现标签。\n2. 标签文本是“(玩家号) 玩家名”，按数字键 1-9 回滚对应玩家那一条。\n3. 滚轮上滚 = 回滚更早的放置，下滚 = 回到最新的放置；松手退出。",
                    "1. Hold the selection key: every player's latest placement gets a label.\n2. The label reads \"(player number) name\"; press 1-9 to roll back that player's entry.\n3. Wheel up picks an older placement, wheel down returns to the newest; release to exit.");
                GUILayout.Label(steps, SRApi.Ctl.LabelWrap, GUILayout.Width(width), GUILayout.Height(SRApi.Ctl.TextHeight(steps, width)));
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.页面", __ex); }
        }

        // ---------------------------- 注册 ----------------------------

        private static void SelfReg() {
            SRApi.LocSec(Sec, "方块破坏", null);
            SRApi.Nav(Sec, 60); // 侧栏栏目顺序
            SRApi.LocKey(Sec, KeySelection, "选择键", null);
            SRApi.LocDesc(Sec, KeySelection,
                "方块破坏的选择键\n按住显示每位玩家最近放置的方块标签，并按 1-9 回滚\n仅房主可用；松开即退出，不改变对局规则。",
                "selection key for block destruction\nhold it to label each player's latest placement and press 1-9 to roll it back\nhost only; releasing the key exits without changing match rules");
            SRApi.LocKey(Sec, KeyUseSmoke, "使用烟雾动画", null);
            SRApi.LocDesc(Sec, KeyUseSmoke,
                "回滚时的烟雾动画\n开启后销毁方块会播放游戏原生的烟雾特效\n默认关闭；只影响画面表现，不改判定。",
                "smoke animation on rollback\nplays the game's native smoke effect when a rolled-back block is destroyed\noff by default; visual only, does not affect gameplay");
            SRApi.RegisterPage(Sec, "Normal", "普通", "Normal", 10, RenderPage);
        }

        public void Initialize(IFeatureHost plugin) {
            SelfReg();
            _mp = plugin;
            _selectKey = _mp.Config.Bind(Sec, KeySelection, KeyCode.LeftAlt, "方块破坏选择键：按住显示放置标签，按 1-9 回滚（仅房主）。");
            _useSmoke = _mp.Config.Bind(Sec, KeyUseSmoke, false, "回滚时是否播放烟雾动画（默认关闭，仅画面表现）。");
            // 预登记本页快捷键：不预登记则重启后在首次打开本页前按住按键无效（同 id 幂等）。
            SRApi.RegisterKey("方块破坏-选择", _selectKey, "hold");

            // 事件监听（不是 Harmony 补丁）：三类事件都登记同一个收集器。
            Collector collector = new Collector();
            GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(collector, true);
            GameEventManager.ChangeListener<GameEvent.DestroyPieceEvent>(collector, true);
            GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(collector, true);
            SceneManager.activeSceneChanged += OnSceneChanged;

            // 每帧按键 / 标签刷新：核心逐帧 try/catch 调用，功能被总开关禁用时自行早退。
            SRApi.RegisterTick("方块破坏-选择", Tick);

            // 对外能力槽位：调用方经 Slots.Require 按名取用，不引用本类，取不到即降级。
            Slots.Provide(SlotIsPlayerPlaced, (Func<Placeable, bool>)IsPlayerPlaced);
            Slots.Provide(SlotPlayerPlacedBlocks, (Func<List<Placeable>>)PlayerPlacedBlocks);
            Slots.Provide(SlotAttributeToHost, (Action<List<Placeable>>)AttributeToHost);
        }
    }
}
