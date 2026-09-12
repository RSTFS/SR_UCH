using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
    //方块破坏：以 UchTweaks 的 DestroyBlocks 为基础。
    public class DestroyBlocks : ITweak {
        private static MainPlugin _mp;
        private static int _index;
        private static List<Placeable> Blocks = new List<Placeable>();
        private static bool _altDown;
        private static Placeable _selected;
        private static Placeable _tintedBlock; //当前高亮
        private static ConfigEntry<KeyCode> _toggleKey;
        private static ConfigEntry<KeyCode> _deleteKey;
        private static ConfigEntry<SelectMode> _selectMode;
        private static ConfigEntry<ListMode> _listMode;
        private static ConfigEntry<TrackMode> _trackMode;

        public enum SelectMode {
            Placement,
            Distance
        }

        //列表模式：普通只列有玩家放置记录的方块，进阶列出全部方块。
        public enum ListMode {
            Normal,
            Advanced
        }

        //追踪玩家：不追踪=全部；#1-#4=只找对应玩家号的方块（仅列表模式相关）。
        public enum TrackMode {
            NoTrack,
            P1,
            P2,
            P3,
            P4
        }

        //「允许客户端删除」是外部模块的功能（配置/开关/快捷键/同步都在那边）；外部模块初始化时注册取值委托。
        //SR 侧只保留"这个能力是否被允许"的接点：未安装外部模块或未开启 → 房客不能删（原版行为）。
        private static Func<bool> _allowClientsProvider;
        public static void SetAllowClientsProvider(Func<bool> provider) { _allowClientsProvider = provider; }
        public static bool AllowClientsOn { get { return _allowClientsProvider != null && _allowClientsProvider(); } }
        //追踪玩家仅在列表模式=普通时显示。
        public static bool TrackPlayerVisible { get { return _listMode == null || _listMode.Value == ListMode.Normal; } }
        private static Dictionary<Placeable, PlacementInfo> _placements = new Dictionary<Placeable, PlacementInfo>();
        private static GameObject _infoTag;
        private static Text _infoText;
        private static Placeable _infoTarget;
        private static bool _infoTagTried;
        public static bool Enabled = true;

        public class PlacementInfo {
            public int playerNumber;
            public string playerName;
            public Color color;
        }

        public class PlacementListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                GameEvent.PiecePlacedEvent ppe = e as GameEvent.PiecePlacedEvent;
                if (ppe != null) {
                    if (ppe.PlacedBlock == null) return;
                    PlacementInfo info = new PlacementInfo();
                    info.playerNumber = ppe.PlayerNumber;
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        LobbyPlayer lp = lm.GetLobbyPlayer(ppe.PlayerNumber);
                        if (lp != null) {
                            info.playerName = lp.playerName;
                            info.color = lp.NetworkPlayerColor;
                        }
                    }
                    if (string.IsNullOrEmpty(info.playerName)) info.playerName = "Player " + info.playerNumber;
                    _placements[ppe.PlacedBlock] = info;
                    return;
                }
                GameEvent.DestroyPieceEvent dpe = e as GameEvent.DestroyPieceEvent;
                if (dpe != null && dpe.Piece != null) {
                    _placements.Remove(dpe.Piece);
                }
            }
        }

        //PiecePlacedEvent 只在放置者本地触发（游戏按 networkNumber 过滤、其他端不重放），
        //房主端因此缺房客的方块；网络消息所有端都收，故从这里补记录。
        public class NetworkPlacementListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
                    if (nm == null || nm.Message == null) return;
                    if (nm.Message.msgType != NetMsgTypes.PiecePlaced) return;
                    MsgPiecePlaced msg = nm.ReadMessage as MsgPiecePlaced;
                    if (msg == null || msg.PieceID == 0) return;
                    Placeable p = FindPlaceableByID(msg.PieceID);
                    if (p == null) return;
                    PlacementInfo info = new PlacementInfo();
                    info.playerNumber = msg.PlayerNumber;
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        LobbyPlayer lp = lm.GetLobbyPlayer(msg.PlayerNumber);
                        if (lp != null) {
                            info.playerName = lp.playerName;
                            info.color = lp.NetworkPlayerColor;
                        }
                    }
                    if (string.IsNullOrEmpty(info.playerName)) info.playerName = "Player " + info.playerNumber;
                    _placements[p] = info;
                } catch (Exception __ex) { SR.Guard.Log("记录方块放置者", __ex); }
            }

            private static Placeable FindPlaceableByID(int id) {
                foreach (Placeable p in Placeable.AllPlaceables) {
                    if (p != null && p.ID == id) return p;
                }
                return null;
            }
        }

        private static void SelfReg() {
            SR.RowFilters.Add(TrackPlayerRowVisible); //通用条目行扩展点：「追踪玩家」只在列表模式=普通时显示
            SR.LocSec("Destroy Blocks", "方块破坏", null);
            SR.Nav("Destroy Blocks", 50); //侧栏栏目顺序 50
            SR.LocKey("Destroy Blocks", "Toggle Key", "切换键", null);
            SR.LocDesc("Destroy Blocks", "Toggle Key", "按住进入删除模式并高亮最近放置的方块（松开退出）", "Hold to enter delete mode and highlight the most recently placed block (release to exit)");
            SR.LocKey("Destroy Blocks", "Delete Key", "删除键", null);
            SR.LocDesc("Destroy Blocks", "Delete Key", "删除当前选中的方块（鼠标滚轮切换选择）", "Delete the currently selected block (mouse wheel cycles selection)");
            SR.LocKey("Destroy Blocks", "Enabled", "方块破坏总开关", null);
            SR.LocDesc("Destroy Blocks", "Enabled", "", "");
            SR.LocKey("Destroy Blocks", "Select Mode", "选择模式", null);
            SR.LocDesc("Destroy Blocks", "Select Mode", "选择模式：距离 = 按离自己距离排序（初始最近）；放置顺序 = 最后放的先选", "Select mode: Distance = sorted by distance to you (nearest first); Placement = most recently placed first");
            SR.LocKey("Destroy Blocks", "List Mode", "列表模式", null);
            SR.LocDesc("Destroy Blocks", "List Mode", "列表模式：普通 = 只列出玩家确切放置过的方块；进阶 = 所有方块单独列出", "List mode: Normal = only blocks actually placed by players; Advanced = every block listed individually");
            SR.LocKey("Destroy Blocks", "Track Player", "追踪玩家", null);
            SR.LocDesc("Destroy Blocks", "Track Player", "追踪玩家：不追踪 = 找所有玩家的方块；#1 = 只找玩家1的方块，#2/#3/#4 以此类推（只影响列表，配合列表模式使用）", "Track player: NoTrack = find every player's blocks; #1 = only blocks placed by player 1, #2/#3/#4 likewise (affects the list, used with list mode)");
        }

        //「追踪玩家」只在列表模式=普通时显示（进阶模式下列出所有方块，不需要它）
        private static bool TrackPlayerRowVisible(ConfigEntryBase e) {
            if (e.Definition.Section != "Destroy Blocks" || e.Definition.Key != "Track Player") return true;
            return TrackPlayerVisible;
        }

        public void Initialize(MainPlugin plugin) {
            SelfReg();
            _mp = plugin;
            ConfigEntry<bool> enabled = _mp.Config.Bind("Destroy Blocks", "Enabled", false, "方块破坏功能总开关（仅房主可用）");
            Enabled = enabled.Value;
            enabled.SettingChanged += (s, e) => Enabled = enabled.Value;
            _toggleKey = _mp.Config.Bind(
                "Destroy Blocks",
                "Toggle Key",
                KeyCode.LeftAlt,
                "Keybind for holding to enter destroy mode (also highlights the block to delete)");
            _deleteKey = _mp.Config.Bind(
                "Destroy Blocks",
                "Delete Key",
                KeyCode.Backspace,
                "Keybind for deleting the currently selected block");
            SR.RegisterKey("方块破坏-切换", _toggleKey, "hold");
            SR.RegisterKey("方块破坏-删除", _deleteKey, "press");
            _selectMode = _mp.Config.Bind(
                "Destroy Blocks",
                "Select Mode",
                SelectMode.Distance,
                "选择模式：距离 = 方块按离自己的距离排序，初始选最近的，滚轮从近到远；放置顺序 = 最后放的先选，依次往回。");
            _listMode = _mp.Config.Bind(
                "Destroy Blocks",
                "List Mode",
                ListMode.Normal,
                "列表模式：普通 = 只列出玩家确切放置过的方块（关卡初始布局/系统方块不出现）；进阶 = 所有方块单独列出，不在乎有没有玩家号。");
            _trackMode = _mp.Config.Bind(
                "Destroy Blocks",
                "Track Player",
                TrackMode.NoTrack,
                "追踪玩家：不追踪 = 找所有玩家的方块；#1-#4 = 只找对应玩家号的方块。");

            Harmony.CreateAndPatchAll(typeof(DestroyBlocks));
            PlacementListener listener = new PlacementListener();
            GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(listener, true);
            GameEventManager.ChangeListener<GameEvent.DestroyPieceEvent>(listener, true);
            //补记"非本端放置"的方块归属，供追踪模式按玩家号筛选。
            NetworkPlacementListener netListener = new NetworkPlacementListener();
            GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(netListener, true);
            SceneManager.activeSceneChanged += OnSceneChanged;
        }

        private static void OnSceneChanged(Scene a, Scene b) {
            _placements.Clear();
            DestroyInfoTag();
        }

        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPrefix]
        static void Controls(GameControl __instance) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            //只允许派对/创意模式；已接入 IgnoreModeLimit 豁免。
            if (!SR.GateModeAllows(SR.ModeMask.Party | SR.ModeMask.Creative)) return;
            if (!AllowClientsOn) {
                if (Matchmaker.CurrentMatchmakingLobby is GamesparksMatchmakingLobby gml && !gml.IsOwner) return;
            }
            if (SR.ComboKeyDown(_toggleKey)) {
                _altDown = true;
                RebuildList();
                if (Blocks.Count <= 0) return;
                if (_selectMode != null && _selectMode.Value == SelectMode.Distance) {
                    SortByDistance(); //距离模式：近→远
                    _index = 0;
                } else {
                    _index = Blocks.Count - 1;
                }
                _selected = Blocks[_index];
            }
            if (SR.ComboKeyHeld(_toggleKey)) 
            {
 
                if (Blocks.Count <= 0) return;
                Placeable prev = _selected;
                if (prev != null) {
                    for (int i = 0; i < Blocks.Count; i++) {
                        if (Blocks[i] == prev) { _index = i; break; }
                    }
                }
                if (_index < 0 || _index >= Blocks.Count) _index = Blocks.Count - 1;
                if (SR.ComboKeyDown(_deleteKey)) {
                    Placeable target = Blocks[_index];
                    if (target == null || target.MarkedForDestruction) {
                        Blocks.RemoveAt(_index);
                        if (_index >= Blocks.Count) _index = Blocks.Count - 1;
                        _tintedBlock = null;
                        return;
                    }

                    if (!IsHostNow() && AllowClientsOn) {
                        BroadcastPieceDestroyed(target);
                    }
                    target.DestroySelf();
                    target.OnDestroy();
                    Blocks.RemoveAt(_index);
                    _index--;
                    if (_index < 0) _index = 0;
                    _selected = null;
                    _tintedBlock = null; 
                    if (Blocks.Count <= 0) return;
                }
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (wheel != 0f) {
                    _index += wheel > 0f ? 1 : -1;
                }
                if (_index >= Blocks.Count) _index = 0;
                if (_index < 0) _index = Blocks.Count - 1;
                _selected = Blocks[_index];
                if (_selected != _tintedBlock) {
                    if (_tintedBlock != null) {
                        try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.GetAxis", __ex); }
                    }
                    try { _selected.AddBombTint(new Color(255, 255, 255, 10)); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.RemoveBombTint", __ex); }
                    _tintedBlock = _selected;
                }
                try { _selected.Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.AddBombTint", __ex); } //每帧应用高亮色（bombTints>0 → 白色）
                UpdateInfoTag(_selected);
            } 
            else if(_altDown)
            {
                _altDown = false;
                if (_tintedBlock != null) {
                    try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.UpdateInfoTag", __ex); }
                }
                _tintedBlock = null;
                _selected = null;
                DestroyInfoTag();
                RebuildList();
                if (Blocks.Count <= 0) return;
                if (_index < 0 || _index >= Blocks.Count) _index = Blocks.Count - 1;
                Blocks[_index].Tint();
            }
        }

        private static void SortByDistance() {
            try {
                Vector3 me = LocalPlayerPos();
                Blocks.Sort((a, b) => {
                    float da = (a.transform.position - me).sqrMagnitude;
                    float db = (b.transform.position - me).sqrMagnitude;
                    return da.CompareTo(db);
                });
            } catch (Exception __ex) { SR.Guard.Log("按距离排序方块", __ex); }
        }

        private static Vector3 LocalPlayerPos() {
            try {
                foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                    if (c != null && c.hasAuthority) return c.transform.position;
                }
            } catch (Exception __ex) { SR.Guard.Log("获取本地角色位置", __ex); }
            return Vector3.zero;
        }

        private static void RebuildList() {
            Blocks.Clear();
            bool normalOnly = _listMode == null || _listMode.Value == ListMode.Normal;
            foreach (Placeable p in Placeable.AllPlaceables) {
                if (p == null) continue;
                //DestroySelf（其它 mod/游戏也用）只隐藏渲染器、不把对象移出 AllPlaceables，
                //故 activeSelf 检查不够，MarkedForDestruction 才是可靠信号。
                if (p.MarkedForDestruction) continue;
                if (!p.gameObject.activeSelf) continue;
                if (p.Name.Contains("SetPiece")) continue;
                if (p.Name.Contains("Goal Block")) continue;
                if (p.Name.Contains("Start Plank")) continue;
                if (p.isSetPiece) continue;
                if (normalOnly && !_placements.ContainsKey(p)) continue;
                if (_trackMode != null && _trackMode.Value != TrackMode.NoTrack) {
                    PlacementInfo pi = null;
                    _placements.TryGetValue(p, out pi);
                    if (pi == null || pi.playerNumber != (int)_trackMode.Value) continue;
                }
                Blocks.Add(p);
            }
        }

        //在选中方块上方显示浮动名牌："Name (#number)"，按玩家颜色着色。
        private static void UpdateInfoTag(Placeable p) {
            if (p == _infoTarget) {
                if (_infoTag != null) _infoTag.transform.position = p.transform.position + new Vector3(0f, 1.5f, 0f);
                return;
            }
            _infoTarget = p;
            if (!EnsureInfoTag()) return;
            PlacementInfo info = null;
            _placements.TryGetValue(p, out info);
            string label = info != null ? "#" + info.playerNumber + " " + info.playerName : "Unknown";
            if (_infoText != null) {
                _infoText.text = label;
                if (info != null) _infoText.color = info.color;
            }
            if (_infoTag != null) _infoTag.transform.position = p.transform.position + new Vector3(0f, 1.5f, 0f);
        }

        private static bool EnsureInfoTag() {
            if (_infoTag != null) return true;
            if (_infoTagTried) return false;
            _infoTagTried = true;
            //从角色光标复制名牌（与 RemovePlayerPlacements 同法）。
            foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                if (c == null) continue;
                GamePlayer gp = c.AssociatedGamePlayer;
                if (gp == null || gp.CursorInstance == null) continue;
                if (gp.CursorInstance.nameTag == null) continue;
                GameObject prefab = gp.CursorInstance.nameTag.gameObject;
                GameObject go = (GameObject)UnityEngine.Object.Instantiate(prefab);
                NameTag nt = go.GetComponent<NameTag>();
                if (nt != null) {
                    nt.currentAlpha = 1f;
                    if (nt.nameCanvasGroup != null) nt.nameCanvasGroup.alpha = 1f;
                    if (nt.canvas != null) nt.canvas.sortingOrder = 32767;
                }
                go.transform.SetParent(null);
                _infoText = go.GetComponentInChildren<Text>();
                _infoTag = go;
                return true;
            }
            return false;
        }

        private static void BroadcastPieceDestroyed(Placeable p) {
            try {
                if (p == null) return;
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.client == null || !lm.client.isConnected) return;
                MsgPieceDestroyed msg = new MsgPieceDestroyed {
                    BlockID = p.ID,
                    SceneLoadNumber = LobbyManagerManager.Instance.SceneLoadCounter,
                    MachineNetworkNumber = MyNetworkNumber()
                };
                lm.client.Send(NetMsgTypes.PieceDestroyed, msg);
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("删除方块同步失败: " + ex.Message);
            }
        }

        //供 EX「清除地图对象」调用：依据 _placements 记录，只清玩家放置的道具、不误删关卡布局。
        //逐个广播 PieceDestroyed 后本地销毁：服务器转发全员，房客也能用（与客户端删除同一通道）。
        public static int ClearAllPlayerPlacements() {
            int n = 0;
            try {
                List<Placeable> list = new List<Placeable>(_placements.Keys);
                for (int i = 0; i < list.Count; i++) {
                    Placeable p = list[i];
                    if (p == null || p.MarkedForDestruction) continue;
                    try {
                        BroadcastPieceDestroyed(p);
                        p.DestroySelf();
                        p.OnDestroy();
                        n++;
                    } catch (Exception __ex) { SR.Guard.Log("清除地图对象(单个销毁)", __ex); }
                }
                _placements.Clear();
                try { Blocks.Clear(); } catch { } //同步清空候选表，避免残留已销毁引用
            } catch (Exception __ex) { SR.Guard.Log("清除地图对象", __ex); }
            return n;
        }

        private static bool IsHostNow() {
            //统一房主判定，见 SR.Gate.Service.cs（离线/无连接也算权威）。
            return SR.IsHost;
        }

        private static int MyNetworkNumber() {
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null) continue;
                    Character c = p.PlayerCharacter;
                    if (c != null && c.hasAuthority) return c.networkNumber;
                }
            } catch (Exception __ex) { SR.Guard.Log("获取本地 networkNumber", __ex); }
            return -1;
        }

        private static void DestroyInfoTag() {
            if (_infoTag != null) {
                UnityEngine.Object.Destroy(_infoTag);
                _infoTag = null;
            }
            _infoText = null;
            _infoTarget = null;
            _infoTagTried = false;
        }
    }
}
