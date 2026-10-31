// 方块破坏：列出现场方块、按玩家归属筛选、远程销毁。
// 槽位（对外 Provide）：DestroyBlocks.IsPlayerPlaced / PlayerPlacedBlocks / AttributeToHost。
// 权限模型：默认只有房主能真正删除；房客可以用同样的键高亮、选中、看信息条，按删除键不生效。
// 扩展点：外部模组可通过槽位 Blocks.AllowClientsOn 提供 Func<bool>，返回 true 时房客也能删除（含网络同步）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.destroyblocks", "DestroyBlocks", "1.0", Provides = new string[] {
        "DestroyBlocks.IsPlayerPlaced", "DestroyBlocks.PlayerPlacedBlocks", "DestroyBlocks.AttributeToHost" })]
    public class DestroyBlocks : ITweak {
        private static IFeatureHost _mp; // 仅用于读 Config（契约类型）
        private static int _index;
        private static List<Placeable> Blocks = new List<Placeable>();
        private static bool _altDown;
        private static Placeable _selected;
        private static GameObject _infoTag;
        private static Text _infoText;
        private static Placeable _infoTarget;
        private static bool _infoTagTried;
        private static Placeable _tintedBlock; // 当前高亮方块
        private static ConfigEntry<KeyCode> _toggleKey;
        private static ConfigEntry<KeyCode> _deleteKey;
        private static ConfigEntry<SelectMode> _selectMode;
        private static ConfigEntry<ListMode> _listMode;
        private static ConfigEntry<TrackMode> _trackMode;
        private static ConfigEntry<RefreshMode> _refreshMode;


        public enum SelectMode {
            Placement,
            Distance
        }

        // 列表模式：普通只列有玩家放置记录的方块，进阶列出全部方块。
        public enum ListMode {
            Normal,
            Advanced
        }

        // 追踪玩家：不追踪 = 全部；#1-#4 = 只找对应玩家号的方块（仅普通列表模式相关）。
        public enum TrackMode {
            NoTrack,
            P1,
            P2,
            P3,
            P4
        }

        // 列表刷新方式（按住切换键期间）：实时 = 候选表持续保持最新，别人刚放/刚删即时可见；
        // 暂存 = 只在按下切换键时重建一次，整段按住期间不再扫描，开销最小。
        public enum RefreshMode {
            Realtime,
            Snapshot
        }

        // 实时列表的重建节流：事件标脏后最快 RealtimeMinInterval 重建一次，避免每帧遍历上千个 AllPlaceables。
        // RealtimeFallbackInterval 兜底重建事件未覆盖的变化（重载 / 快照重建）。
        private const float RealtimeMinInterval = 0.1f;
        private const float RealtimeFallbackInterval = 0.5f;
        private static bool _listDirty; // 有变化待刷新（由放置/销毁事件置位）
        private static float _lastListRebuildAt = -999f; // 上次全量重建的时刻

        // 追踪玩家仅在列表模式 = 普通时显示。
        public static bool TrackPlayerVisible { get { return _listMode == null || _listMode.Value == ListMode.Normal; } }
        // 以 Placeable 为键（Unity 对象作字典键）：对象被 OnDisable/Destroy 掉却没走 DestroyPieceEvent 时会永久留在表里，
        // 既拖住 GC，又让 PlayerPlacedBlocks 返回「假存活」的对象（audit P0-17）。这里加周期修剪。
        private static Dictionary<Placeable, PlacementInfo> _placements = new Dictionary<Placeable, PlacementInfo>();
        private static float _placementsPrunedAt = -999f;
        private const float PlacementsPruneInterval = 30f;
        // 把快照加载出来的方块登记为房主（1 号）放置；由关卡功能经 DestroyBlocks.AttributeToHost 槽位调用。
        public static void AttributeToHost(List<Placeable> blocks) {
            try {
                if (blocks == null || blocks.Count == 0) return;
                string hostName = null;
                // 必须一并填 color：PlacementInfo.color 默认 (0,0,0,0)，UpdateInfoTag 会无条件赋值，漏填会让名牌文字全透明。
                Color hostColor = Color.white;
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        LobbyPlayer lp = lm.GetLobbyPlayer(1);
                        if (lp != null) {
                            hostName = lp.playerName;
                            hostColor = lp.NetworkPlayerColor;
                        }
                    }
                } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (string.IsNullOrEmpty(hostName)) hostName = "Host";
                for (int i = 0; i < blocks.Count; i++) {
                    Placeable p = blocks[i];
                    if (p == null) continue;
                    PlacementInfo info = new PlacementInfo();
                    info.playerNumber = 1;
                    info.playerName = hostName;
                    info.color = hostColor;
                    _placements[p] = info;
                }
                _listDirty = true;
            } catch (Exception __ex) { SRApi.Guard.Log("构建对象归属房主", __ex); }
        }

        public static bool Enabled = true;

        public class PlacementInfo {
            public int playerNumber;
            public string playerName;
            public Color color;
        }

        // 临时道具（炸弹，放置后自爆）记成“玩家放置”会在普通列表留下永远删不到的幽灵项。
        // PlayerNumber <= 0 表示非玩家放置（系统 / 关卡自带件）。
        private static readonly string[] _tmpPieces = new string[] { "Bomb Mini", "3x3 Bomb", "Bomb Mega" };

        private static bool IsPlayerPlacement(Placeable p, int playerNumber) {
            if (p == null || playerNumber <= 0) return false;
            string n = p.Name;
            if (string.IsNullOrEmpty(n)) return true;
            for (int i = 0; i < _tmpPieces.Length; i++) {
                if (n == _tmpPieces[i]) return false;
            }
            return true;
        }

        // 普通页（本栏目唯一页面）：直接渲染本栏目的通用设置条目。
        private static void RenderNormalPage() {
            SRApi.RenderSectionEntries("Destroy Blocks");
        }


        // 方块破坏的唯一实现，保证各入口行为一致。
        // DestroySelf 自带网络同步（发 MsgPieceDestroyed），不可再显式广播以免重复。
        private static void DestroyBlockNative(int index) {
            try {
                if (index < 0 || index >= Blocks.Count) return;
                Placeable target = Blocks[index];
                if (target == null) return;
                // 与旧版一致的销毁路径：DestroySelf 自带网络同步；补一次 OnDestroy 立刻摘干净。
                target.DestroySelf();
                target.OnDestroy();
                _placements.Remove(target); // 本路径不发 DestroyPieceEvent，必须自己清记录，避免强引用已销毁对象
            } catch (Exception __ex) { SRApi.Guard.Log("方块破坏.原生销毁", __ex); }
        }


        // 多对象道具：游戏只为“主体”发 PiecePlacedEvent，子元件需沿 Placeable.ParentPiece 向上找归属。
        private static PlacementInfo ResolveInfo(Placeable p) {
            if (p == null) return null;
            PlacementInfo info;
            if (_placements.TryGetValue(p, out info)) return info;
            try {
                Placeable cur = p;
                for (int i = 0; i < 8; i++) {
                    Placeable up = cur.ParentPiece;
                    if (up == null || up == cur) break;
                    if (_placements.TryGetValue(up, out info)) return info;
                    cur = up;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("解析子元件归属", __ex); }
            return null;
        }

        public class PlacementListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    GameEvent.PiecePlacedEvent ppe = e as GameEvent.PiecePlacedEvent;
                    if (ppe != null) {
                        if (!IsPlayerPlacement(ppe.PlacedBlock, ppe.PlayerNumber)) return;
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
                        _listDirty = true; // 实时列表：候选表待刷新
                        return;
                    }
                    GameEvent.DestroyPieceEvent dpe = e as GameEvent.DestroyPieceEvent;
                    if (dpe != null && dpe.Piece != null) {
                        _placements.Remove(dpe.Piece);
                        _listDirty = true; // 方块没了，候选表要跟着变
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("记录方块放置者(本地)", __ex); }
            }
        }

        // PiecePlacedEvent 只在放置者本地触发，房主端缺房客的方块；PiecePlaced 网络消息所有端都收，故从这里补记录。
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
                    if (!IsPlayerPlacement(p, msg.PlayerNumber)) return;
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
                    _listDirty = true; // 别人放的方块：实时列表要立刻看到
                } catch (Exception __ex) { SRApi.Guard.Log("记录方块放置者", __ex); }
            }

        private static Placeable FindPlaceableByID(int id) {
                foreach (Placeable p in Placeable.AllPlaceables) {
                    // 必须过滤 MarkedForDestruction：DestroySelf 后的对象仍留在 AllPlaceables，只判 null + ID 会把死方块当作有效目标。
                    if (p != null && !p.MarkedForDestruction && p.ID == id) return p;
                }
                return null;
            }
        }

        private static void SelfReg() {
            SRApi.RowFilters.Add(TrackPlayerRowVisible); // 通用条目行扩展点
            SRApi.LocSec("Destroy Blocks", "方块破坏", null);
            SRApi.Nav("Destroy Blocks", 60); // 侧栏栏目顺序
            SRApi.LocKey("Destroy Blocks", "Toggle Key", "切换键", null);
            SRApi.LocDesc("Destroy Blocks", "Toggle Key", "删除模式按键\n按住高亮候选方块（房客同样可用）\n可改绑，支持组合键", "Delete-mode key\nhold to highlight candidates (guests can use it too)\nrebindable, combos supported");
            SRApi.LocKey("Destroy Blocks", "Delete Key", "删除键", null);
            SRApi.LocDesc("Destroy Blocks", "Delete Key", "删除按键\n删除当前选中的方块（仅房主）\n房客按下只提示，不做删除；滚轮换目标", "Delete key\ndeletes the selected block (host only)\nguests only get a hint; the wheel cycles targets");
            SRApi.LocKey("Destroy Blocks", "Enabled", "方块破坏总开关", null);
            SRApi.LocDesc("Destroy Blocks", "Enabled", "方块破坏总开关\n开启后本页功能才生效\n默认仅房主可删；房客可高亮标记", "Block breaking master switch\nthe page works only while this is on\nhost-only by default; guests can highlight");
            SRApi.LocKey("Destroy Blocks", "Select Mode", "选择模式", null);
            SRApi.LocDesc("Destroy Blocks", "Select Mode", "候选排序方式\n距离＝离你近的优先；放置顺序＝新放的优先\n只影响选择顺序", "Candidate ordering\ndistance = nearest first; placement = newest first\nselection order only");
            SRApi.LocKey("Destroy Blocks", "List Mode", "列表模式", null);
            SRApi.LocDesc("Destroy Blocks", "List Mode", "列表模式\n普通＝只列玩家放置的方块；进阶＝场上所有方块\n普通模式排除炸弹等临时物件", "List mode\nnormal = player-placed blocks only; advanced = every block\nnormal excludes temporary items such as bombs");
            SRApi.LocKey("Destroy Blocks", "Track Player", "追踪玩家", null);
            SRApi.LocDesc("Destroy Blocks", "Track Player", "按玩家号过滤\n不追踪＝全部；1-4＝只看该玩家\n只影响列表，需配合列表模式", "Player filter\nno-track = everyone; 1-4 = that player only\nlist only; works with list mode");
            SRApi.LocEnum("Realtime", "实时");
            SRApi.LocEnum("Snapshot", "暂存");
            SRApi.LocKey("Destroy Blocks", "Refresh Mode", "列表刷新", null);
            SRApi.LocDesc("Destroy Blocks", "Refresh Mode", "候选列表刷新方式\n实时＝按住时持续更新；暂存＝按下时重建一次\n实时已节流，只在按住时有开销", "Candidate refresh\nrealtime = updates while held; snapshot = built once on press\nrealtime is throttled and only costs while held");
            // 「破坏动画/烟雾」选项已删除：它从未被读取（audit 19-P0-1），留着等于假开关。
        }

        // 是否走实时列表（默认实时）
        private static bool RealtimeListOn { get { return _refreshMode == null || _refreshMode.Value == RefreshMode.Realtime; } }

        private static bool TrackPlayerRowVisible(ConfigEntryBase e) {
            if (e.Definition.Section != "Destroy Blocks" || e.Definition.Key != "Track Player") return true;
            return TrackPlayerVisible;
        }

        public void Initialize(IFeatureHost plugin) {
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
            SRApi.RegisterKey("方块破坏-切换", _toggleKey, "hold");
            SRApi.RegisterKey("方块破坏-删除", _deleteKey, "press");
            // 本功能只有一个页面（原高级页已整体删除）。
            SRApi.RegisterPage("Destroy Blocks", "Normal", "普通", "Normal", 10, RenderNormalPage);
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
            // 该行可见性取决于列表模式（见 TrackPlayerRowVisible），而条目缓存只按绑定版本失效，故切换时需主动失效。
            _listMode.SettingChanged += (s, e) => SRApi.Ctl.NoteEntryBound();
            _trackMode = _mp.Config.Bind(
                "Destroy Blocks",
                "Track Player",
                TrackMode.NoTrack,
                "追踪玩家：不追踪 = 找所有玩家的方块；#1-#4 = 只找对应玩家号的方块。");
            _refreshMode = _mp.Config.Bind(
                "Destroy Blocks",
                "Refresh Mode",
                RefreshMode.Realtime,
                "列表刷新：实时 = 按住切换键期间候选表持续保持最新（已节流，只在按住时有开销）；暂存 = 只在按下切换键那一刻重建一次。");

            // 过滤条件变了：实时模式下候选表要重新过滤一次。
            _listMode.SettingChanged += (s, e) => _listDirty = true;
            _trackMode.SettingChanged += (s, e) => _listDirty = true;

            // 补丁注册失败不能连带跳过后面的 Listener 注册与场景订阅。
            try { Harmony.CreateAndPatchAll(typeof(DestroyBlocks)); } catch (Exception e) { SRApi.LogError("方块破坏 补丁注册失败: " + e.Message); }
            PlacementListener listener = new PlacementListener();
            GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(listener, true);
            GameEventManager.ChangeListener<GameEvent.DestroyPieceEvent>(listener, true);
            // 补记非本端放置的方块归属，供追踪模式按玩家号筛选。
            NetworkPlacementListener netListener = new NetworkPlacementListener();
            GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(netListener, true);
            SceneManager.activeSceneChanged += OnSceneChanged;

            // 归属登记同样走槽位，避免与关卡功能产生编译期引用（拆分编译会 CS0103）。
            // 三个槽位必须在这里 Provide：Level.Snapshot 经 Slots.Require 取 AttributeToHost，
            // 少了这一行快照方块不会登记房主（列表里看不到、清场也带不走）。
            Slots.Provide(SlotIsPlayerPlaced, (Func<Placeable, bool>)IsPlayerPlaced);
            Slots.Provide(SlotPlayerPlacedBlocks, (Func<List<Placeable>>)PlayerPlacedBlocks);
            Slots.Provide(SlotAttributeToHost, (Action<List<Placeable>>)AttributeToHost);
            // 关机时把自己提供的槽位摘掉：否则宿主代码仍能 Require 到已失效的实现。
            SRApi.OnShutdown("方块破坏/槽位", delegate {
                try {
                    Slots.Retract(SlotIsPlayerPlaced, null);
                    Slots.Retract(SlotPlayerPlacedBlocks, null);
                    Slots.Retract(SlotAttributeToHost, null);
                    SRApi.LogInfo("[方块破坏] 已注销 3 个对外槽位");
                } catch (Exception __rt) { SRApi.Guard.Log("方块破坏/注销槽位", __rt); }
            });
        }


        // 槽位名：与调用方的唯一约定是“名字 + 委托签名”，刻意用 private const，调用方另写自己的常量以免编译期依赖。
        // 名字一经发布不再更改（改需两边同步）。
        // 房客删除放行槽位：外部模组提供 Func<bool>，true = 房客端也可以真正删除。
        // 每次调用都 Require 一次（字典查找，代价可忽略）：放行方可能比本功能初始化更晚。
        private const string SlotAllowClientsOn = "Blocks.AllowClientsOn";
        private static bool AllowClientsOn {
            get {
                try {
                    Func<bool> f = Slots.Require<Func<bool>>(SlotAllowClientsOn);
                    return f != null && f();
                } catch (Exception __ac) { SRApi.Guard.Log("方块破坏/读取房客放行槽位", __ac); return false; }
            }
        }

        private const string SlotIsPlayerPlaced = "DestroyBlocks.IsPlayerPlaced";
        private const string SlotPlayerPlacedBlocks = "DestroyBlocks.PlayerPlacedBlocks";
        private const string SlotAttributeToHost = "DestroyBlocks.AttributeToHost";

        private static void OnSceneChanged(Scene a, Scene b) {
            _placements.Clear();
            // 候选表与选中态跨场景作废：旧场景的 Placeable 已销毁，留着会被当作有效选中项（AddBombTint 会抛异常）。
            // Blocks 只在按住切换键时重建，清空不影响正常流程。
            Blocks.Clear();
            _selected = null;
            _tintedBlock = null;
            _index = 0;
            _listDirty = false;
            _lastListRebuildAt = -999f; // 新场景：下次按住切换键时无条件重建一次
            DestroyInfoTag();
        }

        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPrefix]
        static void Controls(GameControl __instance) {
            try { PrunePlacementsIfDue(); } catch (Exception __pp) { SRApi.Guard.Log("方块破坏/归属表修剪", __pp); }
            if (!SRApi.GateMaster) return;
            if (!Enabled) return;
            if (SRApi.UiOpen && SRApi.BlockInput) return; 
            // 仅派对 / 创意 / 自由建造模式；已接入 IgnoreModeLimit 豁免。
            if (!SRApi.GateModeAllows(SRApi.ModeMask.Party | SRApi.ModeMask.Creative | SRApi.ModeMask.Freeplay)) return;
            // 权限：只有房主能真正删除；房客照样可以按住切换键高亮、滚轮换目标、看信息条（只读）。
            // 房客按删除键时给一次明确提示，避免"按了没反应"的困惑。
            bool canDestroy = SRApi.CanControlLocally || AllowClientsOn;
            if (SRApi.ComboKeyDown(_toggleKey)) {
                _altDown = true;
                RebuildList();
                _lastListRebuildAt = Time.unscaledTime;
                _listDirty = false;
                if (Blocks.Count <= 0) return;
                if (_selectMode != null && _selectMode.Value == SelectMode.Distance) {
                    SortByDistance(); // 距离模式：近 -> 远
                    _index = 0;
                } else {
                    _index = Blocks.Count - 1;
                }
                _selected = Blocks[_index];
            }
            if (SRApi.ComboKeyHeld(_toggleKey)) 
            {
                // 实时列表：按住期间保持候选表最新（事件置脏 + 节流重建）；暂存模式或不按住时完全不跑。
                if (RealtimeListOn) TickRealtimeList();

                if (Blocks.Count <= 0) return;
                Placeable prev = _selected;
                if (prev != null) {
                    for (int i = 0; i < Blocks.Count; i++) {
                        if (Blocks[i] == prev) { _index = i; break; }
                    }
                }
                if (_index < 0 || _index >= Blocks.Count) _index = Blocks.Count - 1;
                // 删除键只对房主生效；ComboKeyDown 是边沿触发，必须只调一次。
                if (SRApi.ComboKeyDown(_deleteKey)) {
                    if (!canDestroy) {
                        SRApi.NotifyT("仅房主可破坏方块：", "Only the host can destroy blocks: ",
                            SRApi.T("房客只能高亮标记（如已由扩展放行请确认其开关）", "guests can only highlight (check the extension's switch if it should allow you)"));
                    } else {
                        Placeable target = Blocks[_index];
                        if (target == null || target.MarkedForDestruction) {
                            Blocks.RemoveAt(_index);
                            if (_index >= Blocks.Count) _index = Blocks.Count - 1;
                            _tintedBlock = null;
                            return;
                        }

                        // DestroySelf 自身会发 MsgPieceDestroyed，再显式广播会发两条（接收端报 "already marked"）。
                        DestroyBlockNative(_index);
                        Blocks.RemoveAt(_index);
                        _index--;
                        if (_index < 0) _index = 0;
                        _selected = null;
                        _tintedBlock = null;
                        if (Blocks.Count <= 0) return;
                    }
                }
                // 滚轮量必须取 SRApi.TakeWheelEvent()：Input.GetAxis("Mouse ScrollWheel") 在本环境恒为 0，滚轮切换永不生效。
                // TakeWheelEvent 由每帧执行的 ManagerUI.OnGUI 从事件 delta 记录，面板关着也有效。
                float wheel = SRApi.TakeWheelEvent();
                if (wheel != 0f) {
                    _index += wheel > 0f ? 1 : -1;
                    SRApi.MarkWheelUsed(); // 滚轮已被选块器吃掉，地图/自由相机让路
                }
                if (_index >= Blocks.Count) _index = 0;
                if (_index < 0) _index = Blocks.Count - 1;
                _selected = Blocks[_index];
                if (_selected != _tintedBlock) {
                    if (_tintedBlock != null) {
                        try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.GetAxis", __ex); }
                    }
                    // Color 的分量是 0..1 浮点而非 0..255 整数，此处按真实意图写不透明白色。
                    try { _selected.AddBombTint(new Color(1f, 1f, 1f, 1f)); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.RemoveBombTint", __ex); }
                    _tintedBlock = _selected;
                }
                try { _selected.Tint(); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.AddBombTint", __ex); } // 每帧应用高亮色（bombTints > 0 -> 白色）
                try { UpdateInfoTag(_selected); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.UpdateInfoTag", __ex); }
            } 
            else if(_altDown)
            {
                _altDown = false;
                if (_tintedBlock != null) {
                    try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.UpdateInfoTag", __ex); }
                }
                _tintedBlock = null;
                _selected = null;
                DestroyInfoTag();
                RebuildList();
                if (Blocks.Count <= 0) return;
                if (_index < 0 || _index >= Blocks.Count) _index = Blocks.Count - 1;
                try { Blocks[_index].Tint(); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.松开重选高亮", __ex); }
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
            } catch (Exception __ex) { SRApi.Guard.Log("按距离排序方块", __ex); }
        }

        // 本地角色位置按帧缓存：距离模式每次重建都要排序，FindObjectsOfType<Character> 不宜反复全场景遍历。
        private static int _localPosFrame = -1;
        private static Vector3 _localPosCache = Vector3.zero;

        private static Vector3 LocalPlayerPos() {
            if (_localPosFrame == Time.frameCount) return _localPosCache;
            _localPosFrame = Time.frameCount;
            _localPosCache = Vector3.zero;
            try {
                foreach (Character c in SRApi.FrameObjects<Character>()) {
                    if (c != null && c.hasAuthority) { _localPosCache = c.transform.position; break; }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("获取本地角色位置", __ex); }
            return _localPosCache;
        }

        // 本方法跑在 GameControl.Update 链路上（按住 Alt 时最坏每秒 10 次），遍历 AllPlaceables 期间别的脚本可能
        // Object.Destroy(p)，MissingReferenceException 不能外泄，整段须兜住。
        private static void RebuildList() {
          try {
            Blocks.Clear();
            bool normalOnly = _listMode == null || _listMode.Value == ListMode.Normal;
            bool trackOn = _trackMode != null && _trackMode.Value != TrackMode.NoTrack;
            // 不追踪的进阶模式用不到放置者信息；ResolveInfo 最坏沿 ParentPiece 上溯 8 层，是整段扫描的最大开销。
            bool needInfo = normalOnly || trackOn;
            foreach (Placeable p in Placeable.AllPlaceables) {
                if (p == null) continue;
                // DestroySelf 只隐藏渲染器、不移出 AllPlaceables，故 activeSelf 检查不够，MarkedForDestruction 才可靠。
                if (p.MarkedForDestruction) continue;
                if (!p.gameObject.activeSelf) continue;
                string name = p.Name;
                if (name == null) name = "";
                if (name.Contains("SetPiece")) continue;
                if (name.Contains("Goal Block")) continue;
                if (name.Contains("Start Plank")) continue;
                if (p.isSetPiece) continue;
                PlacementInfo pi = needInfo ? ResolveInfo(p) : null; // 自身记录，或沿 ParentPiece 找到主体记录
                if (normalOnly && pi == null) continue;
                if (trackOn) {
                    if (pi == null || pi.playerNumber != (int)_trackMode.Value) continue;
                }
                Blocks.Add(p);
            }
          } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.RebuildList", __ex); }
        }

        // 实时列表：按住切换键期间的节流重建，最快 RealtimeMinInterval 一次；不按切换键或暂存模式下不会被调用。
        // 同 RebuildList：跑在 GameControl.Update 链路上，异常不能外泄。
        private static void TickRealtimeList() {
          try {
            float now = Time.unscaledTime;
            // 兜底：放置/销毁事件未覆盖的变化（重载关卡、快照重建）靠固定间隔兜住，不由其它页面的开关决定。
            bool stale = (now - _lastListRebuildAt) >= RealtimeFallbackInterval;
            if (!_listDirty && !stale) return;
            if (now - _lastListRebuildAt < RealtimeMinInterval) return; // 节流：最快 10Hz
            _lastListRebuildAt = now;
            _listDirty = false;
            RefreshListKeepSelection();
          } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.TickRealtimeList", __ex); }
        }

        // 重建候选表并按引用找回当前选中项：实时模式下列表反复重建，不保选中项会导致高亮乱跳。
        // 同 RebuildList：异常会抛进 GameControl.Update 链路，须兜住。
        private static void RefreshListKeepSelection() {
          try {
            Placeable prev = (_selected != null ? _selected : _tintedBlock);
            // 选中项已被标记销毁时不可再高亮（AddBombTint 对已销毁对象会抛异常）
            if (prev != null && prev.MarkedForDestruction) prev = null;
            RebuildList();
            if (_selectMode != null && _selectMode.Value == SelectMode.Distance) SortByDistance();
            if (Blocks.Count <= 0) {
                _index = 0;
                _selected = null;
                if (_tintedBlock != null) {
                    try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.清空高亮", __ex); }
                    _tintedBlock = null;
                }
                return;
            }
            if (prev != null) {
                for (int i = 0; i < Blocks.Count; i++) {
                    if (Blocks[i] == prev) { _index = i; _selected = Blocks[i]; return; }
                }
            }
            // 选中项已不在列表（多半被别人删了）：就地钳制同一下标，跳到头/尾会让选中项乱飞。
            if (_index >= Blocks.Count) _index = Blocks.Count - 1;
            if (_index < 0) _index = 0;
            _selected = Blocks[_index];
          } catch (Exception __ex) { SRApi.Guard.Log("DestroyBlocks.RefreshListKeepSelection", __ex); }
        }



        // 选中方块上方的名牌（参考 RemovePlayerPlacements：克隆角色光标名牌，显示 (玩家号) 名字 + 玩家颜色）。
        private static void UpdateInfoTag(Placeable p) {
            if (p == _infoTarget) {
                if (_infoTag != null) _infoTag.transform.position = p.transform.position + new Vector3(0f, 1.5f, 0f);
                return;
            }
            _infoTarget = p;
            if (!EnsureInfoTag()) return;
            PlacementInfo info = ResolveInfo(p);
            string label = info != null ? "(" + info.playerNumber + ") " + info.playerName : "?";
            if (_infoText != null) {
                _infoText.text = label;
                if (info != null) _infoText.color = info.color;
            }
            if (_infoTag != null) _infoTag.transform.position = p.transform.position + new Vector3(0f, 1.5f, 0f);
        }

        private static bool EnsureInfoTag() {
            if (_infoTag != null) return true;
            if (_infoTagTried) return false;
            foreach (Character c in SRApi.FrameObjects<Character>()) {
                if (c == null) continue;
                GamePlayer gp = c.AssociatedGamePlayer;
                if (gp == null || gp.CursorInstance == null || gp.CursorInstance.nameTag == null) continue;
                GameObject go = (GameObject)UnityEngine.Object.Instantiate(gp.CursorInstance.nameTag.gameObject);
                NameTag nt = go.GetComponent<NameTag>();
                if (nt != null) {
                    nt.currentAlpha = 1f;
                    if (nt.nameCanvasGroup != null) nt.nameCanvasGroup.alpha = 1f;
                    if (nt.canvas != null) nt.canvas.sortingOrder = 32767;
                }
                go.transform.SetParent(null);
                _infoText = go.GetComponentInChildren<Text>();
                _infoTag = go;
                _infoTagTried = true;
                return true;
            }
            _infoTagTried = true;
            return false;
        }

        private static void DestroyInfoTag() {
            if (_infoTag != null) { UnityEngine.Object.Destroy(_infoTag); _infoTag = null; }
            _infoText = null;
            _infoTarget = null;
            _infoTagTried = false;
        }
        // 供外部模块调用：是否为玩家放置的道具（关卡自带布局与系统件不算）。
        public static bool IsPlayerPlaced(Placeable p) {
            try { return ResolveInfo(p) != null; } catch { return false; }
        }

        // 周期修剪归属表：清掉已销毁（== null 对 Unity 对象成立）或已标记销毁的键。
        // 30 秒一次、只在字典非空时遍历，开销可忽略；不在每帧做是为了不干扰按住删除的热路径。
        private static void PrunePlacementsIfDue() {
            float now = Time.unscaledTime;
            if (now - _placementsPrunedAt < PlacementsPruneInterval) return;
            _placementsPrunedAt = now;
            if (_placements.Count == 0) return;
            List<Placeable> dead = null;
            foreach (KeyValuePair<Placeable, PlacementInfo> kv in _placements) {
                Placeable k = kv.Key;
                bool gone = false;
                try { gone = k == null || k.MarkedForDestruction; } catch { gone = true; }
                if (!gone) continue;
                if (dead == null) dead = new List<Placeable>();
                dead.Add(kv.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) {
                try { _placements.Remove(dead[i]); } catch (Exception __rm) { SRApi.Guard.Log("方块破坏/修剪归属键", __rm); }
            }
            try { SRApi.LogInfo("[方块破坏] 归属表修剪 " + dead.Count + " 条（当前 " + _placements.Count + " 条）"); }
            catch (Exception __lg) { SRApi.Guard.Log("方块破坏/修剪日志", __lg); }
        }

        // 玩家放置的方块清单（不销毁），供清场功能使用。
        public static List<Placeable> PlayerPlacedBlocks() {
            List<Placeable> list = new List<Placeable>();
            try {
                foreach (Placeable p in Placeable.AllPlaceables) {
                    if (p == null) continue;
                    if (p.MarkedForDestruction) { _placements.Remove(p); continue; }
                    if (ResolveInfo(p) == null) continue;
                    list.Add(p);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("收集玩家放置方块", __ex); }
            return list;
        }
    }
}
