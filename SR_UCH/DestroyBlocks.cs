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
    //方块破坏：以 UchTweaks 的 DestroyBlocks 为基础
    public class DestroyBlocks : ITweak {
        private static MainPlugin _mp;
        private static int _index;
        private static List<Placeable> Blocks = new List<Placeable>();
        private static bool _altDown;
        private static Placeable _selected;
        private static Placeable _tintedBlock; //当前高亮
        private static ConfigEntry<KeyCode> _toggleKey;
        private static ConfigEntry<KeyCode> _deleteKey;
        private static ConfigEntry<bool> _allowClients;
        private static ConfigEntry<SelectMode> _selectMode;
        private static ConfigEntry<ListMode> _listMode;
        private static ConfigEntry<TrackMode> _trackMode;

        //方块选择模式：距离排序 / 放置顺序
        public enum SelectMode {
            Placement,
            Distance
        }

        //方块列表模式：
        // 普通：只列出确切有玩家放置记录
        // 进阶：所有方块单独列出，不在乎有没有玩家号
        public enum ListMode {
            Normal,
            Advanced
        }

        //追踪玩家（仅列表模式相关）：不追踪 = 找所有玩家的方块；#1-#4 = 只找对应玩家号的方块
        public enum TrackMode {
            NoTrack,
            P1,
            P2,
            P3,
            P4
        }

        public static bool AllowClientsOn { get { return _allowClients != null && _allowClients.Value; } }
        public static void ToggleAllowClients() { if (_allowClients != null) _allowClients.Value = !_allowClients.Value; }
        //追踪玩家仅在列表模式=普通时显示（进阶模式下不出现）
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

        public void Initialize(MainPlugin plugin) {
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
            ModManager.RegisterKey("方块破坏-切换", _toggleKey, "hold");
            ModManager.RegisterKey("方块破坏-删除", _deleteKey, "press");
            _allowClients = _mp.Config.Bind(
                "EX",
                "Allow Clients",
                false,
                "允许客户端删除：非房主玩家也能删除方块（由房主同步）");
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
            SceneManager.activeSceneChanged += OnSceneChanged;
        }

        private static void OnSceneChanged(Scene a, Scene b) {
            _placements.Clear();
            DestroyInfoTag();
        }

        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPrefix]
        static void Controls(GameControl __instance) {
            if (!ModManager.AllEnabled) return;
            if (!Enabled) return;
            if (!Experiments.IsProgressionUnlockedB()) return; //B 组未解锁：自动拦截
            if (ModManager.UiOpen && ModManager.BlockInput) return; 
            if (GameSettings.GetInstance().GameMode == GameState.GameMode.CHALLENGE) return;
            if (!_allowClients.Value) {
                if (Matchmaker.CurrentMatchmakingLobby is GamesparksMatchmakingLobby gml && !gml.IsOwner) return;
            }
            //扫描一次 + 按模式排序，缓存
            if (ModManager.ComboKeyDown(_toggleKey)) {
                _altDown = true;
                RebuildList();
                if (Blocks.Count <= 0) return;
                if (_selectMode != null && _selectMode.Value == SelectMode.Distance) {
                    SortByDistance(); //距离模式：近→远，初始选最近
                    _index = 0;
                } else {
                    _index = Blocks.Count - 1;
                }
                _selected = Blocks[_index];
            }
            if (ModManager.ComboKeyHeld(_toggleKey)) 
            {
 
                if (Blocks.Count <= 0) return;
                Placeable prev = _selected;
                if (prev != null) {
                    for (int i = 0; i < Blocks.Count; i++) {
                        if (Blocks[i] == prev) { _index = i; break; }
                    }
                }
                if (_index < 0 || _index >= Blocks.Count) _index = Blocks.Count - 1;
                if (ModManager.ComboKeyDown(_deleteKey)) {
                    Placeable target = Blocks[_index];
                    if (target == null || target.MarkedForDestruction) {
                        Blocks.RemoveAt(_index);
                        if (_index >= Blocks.Count) _index = Blocks.Count - 1;
                        _tintedBlock = null;
                        return;
                    }

                    if (!IsHostNow() && _allowClients.Value) {
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
                        try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch { } //恢复旧方块
                    }
                    try { _selected.AddBombTint(new Color(255, 255, 255, 10)); } catch { }
                    _tintedBlock = _selected;
                }
                try { _selected.Tint(); } catch { } //每帧应用高亮色（bombTints>0 → 白色）
                UpdateInfoTag(_selected);
            } 
            else if(_altDown)
            {
                _altDown = false;
                if (_tintedBlock != null) {
                    try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch { } //恢复颜色
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
            } catch { }
        }

        private static Vector3 LocalPlayerPos() {
            try {
                foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                    if (c != null && c.hasAuthority) return c.transform.position;
                }
            } catch { }
            return Vector3.zero;
        }

        private static void RebuildList() {
            Blocks.Clear();
            bool normalOnly = _listMode == null || _listMode.Value == ListMode.Normal;
            foreach (Placeable p in Placeable.AllPlaceables) {
                if (p == null) continue;
                //DestroySelf (used by other mods and the game) only disables a placed piece:
                //it hides the renderers but leaves the object in AllPlaceables, so a plain
                //null/activeSelf check is not enough - MarkedForDestruction is the real signal
                if (p.MarkedForDestruction) continue;
                //skip blocks deactivated by other means
                if (!p.gameObject.activeSelf) continue;
                //dont add colored freeplay blocks or invisible walls
                if (p.Name.Contains("SetPiece")) continue;
                //dont add goal or start blocks
                if (p.Name.Contains("Goal Block")) continue;
                if (p.Name.Contains("Start Plank")) continue;
                //dont add colored wires (this also gets fp blocks, but not invis walls)
                if (p.isSetPiece) continue;
                if (normalOnly && !_placements.ContainsKey(p)) continue;
                //追踪玩家：#1-#4 时只找对应玩家号的方块（不追踪 = 不限）
                if (_trackMode != null && _trackMode.Value != TrackMode.NoTrack) {
                    PlacementInfo pi = null;
                    _placements.TryGetValue(p, out pi);
                    if (pi == null || pi.playerNumber != (int)_trackMode.Value) continue;
                }
                Blocks.Add(p);
            }
        }

        //a floating name tag above the selected block: "Name (#number)", colored like the player
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
            //copy a player name tag from any character's cursor (same trick as RemovePlayerPlacements)
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

        private static bool IsHostNow() {
            try {
                if (NetworkServer.active) {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null && lm.IsHost) return true;
                }
            } catch { }
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null || lm.client == null) return true;
                if (!lm.client.isConnected) return true;
            } catch { return true; }
            return false;
        }

        private static int MyNetworkNumber() {
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null) continue;
                    Character c = p.PlayerCharacter;
                    if (c != null && c.hasAuthority) return c.networkNumber;
                }
            } catch { }
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
