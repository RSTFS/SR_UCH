// 方块破坏。列出现场方块、按玩家归属筛选、远程销毁。
// 槽位：要从别人那儿取 "Blocks.AllowClientsOn"；自己对外给 "DestroyBlocks.IsPlayerPlaced"
// 和 "DestroyBlocks.PlayerPlacedBlocks"。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
    //方块破坏：以 UchTweaks 的 DestroyBlocks 为基础。
    public class DestroyBlocks : ITweak {
        private static IFeatureHost _mp;   //只用来读 Config（契约类型）
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
        private static ConfigEntry<RefreshMode> _refreshMode;

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

        //列表刷新方式（按住切换键期间）：
        //实时 = 候选表持续保持最新，别人刚放/刚删的方块即时出现在列表里；
        //暂存 = 只在按下切换键的那一刻重建一次（原行为，整段按住期间不再扫描，开销最小）。
        public enum RefreshMode {
            Realtime,
            Snapshot
        }

        //实时列表的重建节流：
        //- 放置/销毁事件来了立刻标脏，最快 RealtimeMinInterval 重建一次（默认 10Hz），
        //  避免每帧遍历上千个 AllPlaceables（IMGUI 一帧还会跑两趟，等于 120 次/秒）；
        //- 万一有事件没覆盖到的变化（重载/快照重建等），RealtimeFallback 兜底重建，
        //  保证最多半秒就能看到最新状态。
        private const float RealtimeMinInterval = 0.1f;
        private const float RealtimeFallbackInterval = 0.5f;
        private static bool _listDirty;         //有变化待刷新（由放置/销毁事件置位）
        private static float _lastListRebuildAt = -999f; //上次全量重建的时刻

        //允许客户端删除是外部模块的功能（配置/开关/快捷键/同步都在那边）；外部模块初始化时注册取值委托。
        //SR 侧只保留"这个能力是否被允许"的接点：未安装外部模块或未开启 -> 房客不能删（原版行为）。
        //允许客户端删除：由**别的模块**（EX）登记，这里用时拉取，不持有对方类型。
        //为什么不反向推送：EX 在自己 Initialize 里把委托推过来会有**初始化顺序**问题
        //（若本功能还没初始化，那次推送就丢了，开关永远推不过来）。改成用时拉取则顺序无关：
        //谁先起来都行，登记方晚一点也没关系。
        //槽位名不带模块名（"Blocks."），任何模块都能登记。
        //注意：SR.Provide 是**覆盖**语义（不是"多个登记任一为真"）,  同一个槽位重复登记，最后一次生效。
        private const string SlotAllowClients = "Blocks.AllowClientsOn"; // Func<bool>
        private static Func<bool> _allowCached;    //取到后缓存委托（之后只剩一次委托调用）
        public static bool AllowClientsOn {
            get {
                if (_allowCached == null) _allowCached = SR.Require<Func<bool>>(SlotAllowClients);
                Func<bool> f = _allowCached;
                return f != null && f();
            }
        }
        //追踪玩家仅在列表模式=普通时显示。
        public static bool TrackPlayerVisible { get { return _listMode == null || _listMode.Value == ListMode.Normal; } }
        private static Dictionary<Placeable, PlacementInfo> _placements = new Dictionary<Placeable, PlacementInfo>();
        // 把快照加载出来的方块登记为"房主（1号）放置"
        public static void AttributeToHost(List<Placeable> blocks) {
            try {
                if (blocks == null || blocks.Count == 0) return;
                string hostName = null;
                // **R396 S11**：必须一并取房主颜色。PlacementInfo.color 是值类型、默认 (0,0,0,0)，
                //  而同文件的 UpdateInfoTag 会无条件 _infoText.color = info.color
                //  -> 原来这里只填 playerNumber/playerName，alpha=0 让**名牌文字被设成全透明**。
                //  另两个登记入口（PlacementListener / NetworkPlacementListener）都取了 lp.NetworkPlayerColor，只这里漏了。
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
                } catch { }
                if (string.IsNullOrEmpty(hostName)) hostName = "Host";
                for (int i = 0; i < blocks.Count; i++) {
                    Placeable p = blocks[i];
                    if (p == null) continue;
                    PlacementInfo info = new PlacementInfo();
                    info.playerNumber = 1;
                    info.playerName = hostName;
                    info.color = hostColor;   // **R396 S11**：不填则名牌文字全透明
                    _placements[p] = info;
                }
                _listDirty = true;
            } catch (Exception __ex) { SR.Guard.Log("构建对象归属房主", __ex); }
        }

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

        //与 RemovePlayerPlacements 同源的过滤：炸弹是临时道具（放置后自爆），记成"玩家放置"会在普通列表里
        //留下永远删不到的幽灵；PlayerNumber<=0 = 非玩家（系统/关卡自带件）。
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

        //普通页（本栏目现在只有这一个页面）：直接渲染本栏目的通用设置条目。
        //原高级页的专属条目（自动刷新 / 列顺序 / 玩家筛选 / 种类筛选）已随该页整体删除、
        //不再 Bind，故这里不再需要过滤。
        private static void RenderNormalPage() {
            SR.RenderSectionEntries("Destroy Blocks");
        }


        // **"普通方块破坏"的唯一实现**：高级页的破坏与普通删除键都走这里，保证行为完全一致。
        //  DestroySelf 自带网络同步（会发 MsgPieceDestroyed），房主发起、各端一致。
        private static void DestroyBlockNative(int index) {
            try {
                if (index < 0 || index >= Blocks.Count) return;
                Placeable target = Blocks[index];
                if (target == null) return;
                target.DestroySelf();
                // P1-12 复核结论：这里**不是**"重复析构"，别删。
                // 反编译确认 Placeable.DestroySelf() 对**已放置**的方块只做 Disable + 置 MarkedForDestruction，
                // **不销毁 GameObject**（Assembly-CSharp 13237-13241）—— Unity 此刻不会回调 OnDestroy，方块会一直挂在
                // AllPlaceables 上、继续当 GameEvent 监听者，直到下次 PiecePlacedEvent 触发 destroyMarkedPieces 才真回收。
                // 所以要在这里手动跑一遍 OnDestroy 把它立刻摘干净，不然列表里会留下删不掉的幽灵项。
                // 另外必须走**虚方法**调用：Bomb / ActiveBlock 都 override 了 OnDestroy（7322 / 3702），派发才能带上
                // 各自的清理；换成直接写 ChangeListener + AllPlaceables.Remove 会漏掉这些。
                // Placeable.OnDestroy 本身是幂等的，就算之后 Unity 真销毁再调一次也没副作用。
                target.OnDestroy();
                _placements.Remove(target); //本路径不发 DestroyPieceEvent，必须自己清记录，避免强引用已销毁对象
            } catch (Exception __ex) { SR.Guard.Log("方块破坏.原生销毁", __ex); }
        }


        //多对象道具（主体 + 子元件/附属件）：游戏只为"主体"发 PiecePlacedEvent，子元件自己不在记录里。
        //要能列出并删除整件道具的所有部件，故沿 Placeable.ParentPiece 向上找归属。
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
            } catch (Exception __ex) { SR.Guard.Log("解析子元件归属", __ex); }
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
                        _listDirty = true; //实时列表：候选表待刷新
                        return;
                    }
                    GameEvent.DestroyPieceEvent dpe = e as GameEvent.DestroyPieceEvent;
                    if (dpe != null && dpe.Piece != null) {
                        _placements.Remove(dpe.Piece);
                        _listDirty = true; //同上（方块没了，候选表要跟着变）
                    }
                } catch (Exception __ex) { SR.Guard.Log("记录方块放置者(本地)", __ex); }
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
                    _listDirty = true; //别人放的方块：实时列表要能立刻看到
                } catch (Exception __ex) { SR.Guard.Log("记录方块放置者", __ex); }
            }

        private static Placeable FindPlaceableByID(int id) {
                foreach (Placeable p in Placeable.AllPlaceables) {
                    // 必须过滤 MarkedForDestruction（同文件另两处遍历都已过滤，这里漏了）：
                    //  游戏 Placeable.DestroySelf() 对已放置的方块只做 Disable + 置标记，
                    //  **对象仍留在 AllPlaceables 里**，要等下次 PiecePlacedEvent 触发
                    //  destroyMarkedPieces 才真删。在这段"已判死刑但还没回收"的窗口里，
                    //  只判 null + ID 会返回一个死方块 -> 调用点写入 _placements[p] = info，
                    //  等于给已破坏的方块重新登记归属 -> 列表里出现删不掉的幽灵项。
                    if (p != null && !p.MarkedForDestruction && p.ID == id) return p;
                }
                return null;
            }
        }

        private static void SelfReg() {
            SR.RowFilters.Add(TrackPlayerRowVisible); //通用条目行扩展点：追踪玩家只在列表模式=普通时显示
            SR.LocSec("Destroy Blocks", "方块破坏", null);
            SR.Nav("Destroy Blocks", 60); //侧栏栏目顺序 60
            SR.LocKey("Destroy Blocks", "Toggle Key", "切换键", null);
            SR.LocDesc("Destroy Blocks", "Toggle Key", "按住进入删除模式, 最近放的方块会高亮, 松手退出.", "Hold to enter delete mode and highlight the most recently placed block (release to exit)");
            SR.LocKey("Destroy Blocks", "Delete Key", "删除键", null);
            SR.LocDesc("Destroy Blocks", "Delete Key", "删掉当前选中的方块. 滚轮可以在几个之间换.", "Delete the currently selected block (mouse wheel cycles selection)");
            SR.LocKey("Destroy Blocks", "Enabled", "方块破坏总开关", null);
            SR.LocDesc("Destroy Blocks", "Enabled", "这一页的总开关, 关掉就整个功能停用.", "");
            SR.LocKey("Destroy Blocks", "Select Mode", "选择模式", null);
            SR.LocDesc("Destroy Blocks", "Select Mode", "按距离就是离你近的先选, 按放置顺序就是最后放的先选.", "Select mode: Distance = sorted by distance to you (nearest first); Placement = most recently placed first");
            SR.LocKey("Destroy Blocks", "List Mode", "列表模式", null);
            SR.LocDesc("Destroy Blocks", "List Mode", "普通模式只列玩家亲手放的方块, 多物件道具的子部件也算, 炸弹这类临时的不算. 进阶模式把场上所有方块都单独列出来.", "List mode: Normal = only blocks actually placed by players (sub-parts of multi-object props count too, resolved through ParentPiece; temporary items such as bombs are excluded); Advanced = every block listed individually");
            SR.LocKey("Destroy Blocks", "Track Player", "追踪玩家", null);
            SR.LocDesc("Destroy Blocks", "Track Player", "不追踪就找所有人的方块, 填 1 就只找玩家 1 的. 只影响列表, 配合列表模式用.", "Track player: NoTrack = find every player's blocks; #1 = only blocks placed by player 1, #2/#3/#4 likewise (affects the list, used with list mode)");
            SR.LocEnum("Realtime", "实时");
            SR.LocEnum("Snapshot", "暂存");
            SR.LocKey("Destroy Blocks", "Refresh Mode", "列表刷新", null);
            SR.LocDesc("Destroy Blocks", "Refresh Mode", "实时是在你按住切换键时列表一直刷新, 别人刚放的马上能看到, 有节流所以开销不大. 暂存是只在你按下的那一刻建一次, 最省但按住期间看不到别人的变化.", "Refresh mode: Realtime = the candidate list stays up to date while you hold the toggle key (blocks placed/removed by others show up instantly; throttled and event-driven, cost only while held); Snapshot = rebuilt once when you press the key (cheapest, but you won't see others' changes while holding)");
        }

        //是否走实时列表（默认实时）
        private static bool RealtimeListOn { get { return _refreshMode == null || _refreshMode.Value == RefreshMode.Realtime; } }

        //追踪玩家只在列表模式=普通时显示（进阶模式下列出所有方块，不需要它）
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
            SR.RegisterKey("方块破坏-切换", _toggleKey, "hold");
            SR.RegisterKey("方块破坏-删除", _deleteKey, "press");
            //方块破坏只有一个页面（原高级= 候选方块表格已整体删除：不再注册、不再 Bind 其专属配置）。
            SR.RegisterPage("Destroy Blocks", "Normal", "普通", "Normal", 10, RenderNormalPage);
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
            //追踪玩家这一行是否显示取决于列表模式（见 TrackPlayerRowVisible），而条目缓存只按"绑定版本"失效
            //-> 切换列表模式时主动让缓存失效，否则该行会按旧模式残留/消失。
            _listMode.SettingChanged += (s, e) => SR.Ctl.NoteEntryBound();
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
            //列表模式/追踪玩家变了 = 过滤条件变了 -> 实时模式下候选表要重新过滤一次。
            //（列表模式上面已 NoteEntryBound 让条目缓存失效，这里再让候选表变脏。）
            _listMode.SettingChanged += (s, e) => _listDirty = true;
            _trackMode.SettingChanged += (s, e) => _listDirty = true;

            //补丁注册失败不能连带丢掉后面的 Listener 注册与场景订阅（否则方块归属追踪/场景清理全失效）
            try { Harmony.CreateAndPatchAll(typeof(DestroyBlocks)); } catch (Exception e) { SR.LogError("方块破坏 补丁注册失败: " + e.Message); }
            PlacementListener listener = new PlacementListener();
            GameEventManager.ChangeListener<GameEvent.PiecePlacedEvent>(listener, true);
            GameEventManager.ChangeListener<GameEvent.DestroyPieceEvent>(listener, true);
            //补记"非本端放置"的方块归属，供追踪模式按玩家号筛选。
            NetworkPlacementListener netListener = new NetworkPlacementListener();
            GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(netListener, true);
            SceneManager.activeSceneChanged += OnSceneChanged;

            // 对外能力槽位：别的功能/外部模块只经 SR.Require 取，不引用本类
            //这样本功能可以整体独立成 DLL（或整段删除）而不影响调用方：取不到 = 降级。
            SR.Provide(SlotIsPlayerPlaced, (Func<Placeable, bool>)IsPlayerPlaced);
            SR.Provide(SlotPlayerPlacedBlocks, (Func<List<Placeable>>)PlayerPlacedBlocks);
        }

        //槽位名：与调用方（EX）的唯一约定是"名字 + 委托签名"。
        //刻意用 private const 而不是 public：若调用方引用这个常量，就又成了编译期依赖，等于没解耦。
        //调用方写自己的字符串常量，双方各自独立；名字一经发布就不再改（改要两边同步）。
        private const string SlotIsPlayerPlaced = "DestroyBlocks.IsPlayerPlaced";        // Func<Placeable,bool>
        private const string SlotPlayerPlacedBlocks = "DestroyBlocks.PlayerPlacedBlocks"; // Func<List<Placeable>>

        private static void OnSceneChanged(Scene a, Scene b) {
            _placements.Clear();
            //候选表与选中态同样跨场景作废：旧场景的 Placeable 已被销毁，留着会在下次进入删除模式前
            //被当作有效选中项（对已销毁对象 AddBombTint/RemoveBombTint 会抛异常，只靠 try/catch 兜着）。
            //Blocks 只在按住切换键时重建（RebuildList），清空不会影响正常流程。
            Blocks.Clear(); //List.Clear 不会抛异常，无需 try/catch 包装
            _selected = null;
            _tintedBlock = null;
            _index = 0;
            _listDirty = false;
            _lastListRebuildAt = -999f; //新场景：下次按住切换键时无条件重建一次
            DestroyInfoTag();
        }

        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPrefix]
        static void Controls(GameControl __instance) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            //只允许派对/创意模式；已接入 IgnoreModeLimit 豁免。
            if (!SR.GateModeAllows(SR.ModeMask.Party | SR.ModeMask.Creative | SR.ModeMask.Freeplay)) return;
            // 房主限制：只有房主能删。
            // 为什么房客一律不给删，而不是"删了只在本机生效"：SR 侧**没有**方块删除的网络同步通道，
            // 房客删了只有自己这边消失，房主和其他人那边方块还在 —— 这是直接的脱同步 bug，比"不能用"糟得多。
            // 房客想删，得开 EX 的"允许客户端删除"，那条通道自带同步。
            // **有意不接 GateHost**：本功能不接受"无视房主限制"豁免 —— 豁免只解决权限，解决不了同步。
            // 判据用 SR.CanControlLocally（= Env.IsHost，离线时也为 true）。别用类型判定
            // is GamesparksMatchmakingLobby：它在本地派对 / 离线 / 大厅为空时会整体短路成"判定不了"，然后放行。
            // **R396 S2**：与 LobbyTools.DoLaunch 共用同一入口，免得同一语义两处实现漂移。
            if (!AllowClientsOn && !SR.CanControlLocally) return;
            if (SR.ComboKeyDown(_toggleKey)) {
                _altDown = true;
                RebuildList();
                _lastListRebuildAt = Time.unscaledTime;
                _listDirty = false;
                if (Blocks.Count <= 0) return;
                if (_selectMode != null && _selectMode.Value == SelectMode.Distance) {
                    SortByDistance(); //距离模式：近->远
                    _index = 0;
                } else {
                    _index = Blocks.Count - 1;
                }
                _selected = Blocks[_index];
            }
            if (SR.ComboKeyHeld(_toggleKey)) 
            {
                //实时列表：按住期间保持候选表最新（事件置脏 + 节流重建）。
                //暂存模式或没按住切换键时完全不跑，零开销。
                if (RealtimeListOn) TickRealtimeList();

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

                    //DestroySelf 自身就会发 MsgPieceDestroyed；再显式广播会发两条（接收端报 "already marked"）。
                    DestroyBlockNative(_index);
                    Blocks.RemoveAt(_index);
                    _index--;
                    if (_index < 0) _index = 0;
                    _selected = null;
                    _tintedBlock = null; 
                    if (Blocks.Count <= 0) return;
                }
                // **S5 修复**：滚轮量改取 SR.TakeWheelEvent()。
                //  原来读 Input.GetAxis("Mouse ScrollWheel"), 该值在本环境恒为 0
                //  （项目文档 _IMGUI_LESSONS.md 八.2 已定论），而 _index 的唯一改变路径就是这里
                //  -> 滚轮切换方块从来没生效过，永远只能操作初始选中那一块
                //  （放置顺序模式 = 最后放的；距离模式 = 最近的），想删中间的块只能从一端逐块删。
                //  TakeWheelEvent 由每帧总执行的 ManagerUI.OnGUI 从事件 delta 记录，面板关着也有效。
                float wheel = SR.TakeWheelEvent();
                if (wheel != 0f) {
                    _index += wheel > 0f ? 1 : -1;
                    SR.MarkWheelUsed(); //滚轮已被选块器吃掉，地图/自由相机让路
                }
                if (_index >= Blocks.Count) _index = 0;
                if (_index < 0) _index = Blocks.Count - 1;
                _selected = Blocks[_index];
                if (_selected != _tintedBlock) {
                    if (_tintedBlock != null) {
                        try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.GetAxis", __ex); }
                    }
                    // **P2-28**：原来写的是 new Color(255, 255, 255, 10)。Color 的四个分量是 0..1 的浮点，
                    //  不是 0..255 的整数 —— SpriteRenderer.color 赋值时会钳到 [0,1]，
                    //  于是这行实际等于"不透明白色"，但读起来像"白色 + 4% 透明度"，很容易被误改。
                    //  这里按真实意图写成白色（行为与改动前完全一致，只是不再有量纲歧义）。
                    try { _selected.AddBombTint(new Color(1f, 1f, 1f, 1f)); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.RemoveBombTint", __ex); }
                    _tintedBlock = _selected;
                }
                try { _selected.Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.AddBombTint", __ex); } //每帧应用高亮色（bombTints>0 -> 白色）
                try { UpdateInfoTag(_selected); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.UpdateInfoTag", __ex); }
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
                try { Blocks[_index].Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.松开重选高亮", __ex); }
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

        //本地角色位置按帧缓存：距离模式下每次重建列表都要排序，实时模式一秒内还会重建多次，
        //FindObjectsOfType<Character> 没必要每次都把全场景遍历一遍（它本身不便宜）。
        private static int _localPosFrame = -1;
        private static Vector3 _localPosCache = Vector3.zero;

        private static Vector3 LocalPlayerPos() {
            if (_localPosFrame == Time.frameCount) return _localPosCache;
            _localPosFrame = Time.frameCount;
            _localPosCache = Vector3.zero;
            try {
                foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                    if (c != null && c.hasAuthority) { _localPosCache = c.transform.position; break; }
                }
            } catch (Exception __ex) { SR.Guard.Log("获取本地角色位置", __ex); }
            return _localPosCache;
        }

        // R396 S7：本方法**零 try/catch**，而它在按住 Alt 期间最坏每秒被调 10 次
        //  （TickRealtimeList -> RefreshListKeepSelection -> RebuildList），调用点是游戏的
        //  GameControl.Update（里面跑着回合倒计时 / PLACE->PLAY / 回合末结算）。
        //  遍历 AllPlaceables 时读 p.gameObject 与 p.Name 之间，别的脚本完全可能 Object.Destroy(p)
        //  -> MissingReferenceException 直接抛进游戏主循环。整段兜住。
        private static void RebuildList() {
          try {
            Blocks.Clear();
            bool normalOnly = _listMode == null || _listMode.Value == ListMode.Normal;
            bool trackOn = _trackMode != null && _trackMode.Value != TrackMode.NoTrack;
            //进阶 + 不追踪时根本用不到放置者信息：省掉每个方块一次 ResolveInfo。
            //ResolveInfo 最坏要沿 ParentPiece 往上找 8 层 × 字典查询，是整段扫描里最大的开销。
            bool needInfo = normalOnly || trackOn;
            foreach (Placeable p in Placeable.AllPlaceables) {
                if (p == null) continue;
                //DestroySelf（其它 mod/游戏也用）只隐藏渲染器、不把对象移出 AllPlaceables，
                //故 activeSelf 检查不够，MarkedForDestruction 才是可靠信号。
                if (p.MarkedForDestruction) continue;
                if (!p.gameObject.activeSelf) continue;
                //p.Name 每次访问都是一次属性求值，原来这里取了 3 遍 -> 只取一次后复用。
                string name = p.Name;
                if (name == null) name = "";
                if (name.Contains("SetPiece")) continue;
                if (name.Contains("Goal Block")) continue;
                if (name.Contains("Start Plank")) continue;
                if (p.isSetPiece) continue;
                PlacementInfo pi = needInfo ? ResolveInfo(p) : null; //自身记录，或沿 ParentPiece 找到主体记录（多对象道具）
                if (normalOnly && pi == null) continue;
                if (trackOn) {
                    if (pi == null || pi.playerNumber != (int)_trackMode.Value) continue;
                }
                Blocks.Add(p);
            }
          } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.RebuildList", __ex); }
        }

        //实时列表：按住切换键期间的节流重建。
        //只在"有变化（_listDirty）"或"超过兜底间隔"时才重建，且最快 RealtimeMinInterval 一次。
        //不按切换键 / 暂存模式下本方法根本不会被调用 -> 常态开销为零。
        // **R396 S7**：同 RebuildList, 它跑在游戏 GameControl.Update 链路上，异常不能外泄
        private static void TickRealtimeList() {
          try {
            float now = Time.unscaledTime;
            //兜底：放置/销毁事件没覆盖到的变化（重载关卡、快照重建、别人静默改动）靠它兜住。
            // 原来这里被高级页的自动刷新配置项（_autoRefresh，默认关）串在了一起：
            //  高级页删除后用户根本打不开那个开关 -> 实时模式的 0.5 秒兜底**一直是失效的**，
            //  只有事件能触发刷新。兜底是实时模式自身的机制，不该由另一个页面的开关决定。
            bool stale = (now - _lastListRebuildAt) >= RealtimeFallbackInterval;
            if (!_listDirty && !stale) return; //没变化又没过期：什么都不做
            if (now - _lastListRebuildAt < RealtimeMinInterval) return; //节流：最快 10Hz
            _lastListRebuildAt = now;
            _listDirty = false;
            RefreshListKeepSelection();
          } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.TickRealtimeList", __ex); }
        }

        //重建候选表并尽量保住用户当前选中的方块（按引用找回）。
        //实时模式下列表会反复重建，不保选中项的话高亮会在方块之间乱跳，功能等于没法用。
        // **R396 S7**：同 RebuildList（异常会抛进游戏 GameControl.Update 链路）
        private static void RefreshListKeepSelection() {
          try {
            Placeable prev = (_selected != null ? _selected : _tintedBlock);
            //选中项已被别人删掉/标记销毁 -> 不能再高亮它（对已销毁对象 AddBombTint 会抛异常）
            if (prev != null && prev.MarkedForDestruction) prev = null;
            RebuildList();
            if (_selectMode != null && _selectMode.Value == SelectMode.Distance) SortByDistance();
            if (Blocks.Count <= 0) {
                _index = 0;
                _selected = null;
                if (_tintedBlock != null) {
                    try { _tintedBlock.RemoveBombTint(); _tintedBlock.Tint(); } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.清空高亮", __ex); }
                    _tintedBlock = null;
                }
                DestroyInfoTag();
                return;
            }
            if (prev != null) {
                for (int i = 0; i < Blocks.Count; i++) {
                    if (Blocks[i] == prev) { _index = i; _selected = Blocks[i]; return; }
                }
            }
            //选中项已不在列表里（多半被别人删了）：就地落到同一个下标（钳制），
            //不要跳到列表头/尾, 否则删掉一个方块后选中项会乱飞。
            if (_index >= Blocks.Count) _index = Blocks.Count - 1;
            if (_index < 0) _index = 0;
            _selected = Blocks[_index];
          } catch (Exception __ex) { SR.Guard.Log("DestroyBlocks.RefreshListKeepSelection", __ex); }
        }

        //在选中方块上方显示浮动名牌："Name (#number)"，按玩家颜色着色。
        private static void UpdateInfoTag(Placeable p) {
            if (p == _infoTarget) {
                if (_infoTag != null) _infoTag.transform.position = p.transform.position + new Vector3(0f, 1.5f, 0f);
                return;
            }
            _infoTarget = p;
            if (!EnsureInfoTag()) return;
            PlacementInfo info = ResolveInfo(p); //子元件显示所属主体的放置者
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
                _infoTagTried = true;   //只在成功后标记：创建失败时按住期间仍会重试
                return true;
            }
            return false;
        }


        //供 EX清除地图对象调用：依据 _placements 记录，只清玩家放置的道具、不误删关卡布局。
        //供外部模块（EX无视对象）用：这个方块是不是玩家放置的道具（关卡自带布局/系统件不算）。
        //走与列表模式同一个归属解析（含多对象道具的子部件），不暴露内部字典。
        public static bool IsPlayerPlaced(Placeable p) {
            try { return ResolveInfo(p) != null; } catch { return false; }
        }

        //玩家放置的方块清单（不销毁）：供"用快照 <destroyed> 节点清场"使用（各端加载快照时自行销毁 -> 房主房客一致）。
        public static List<Placeable> PlayerPlacedBlocks() {
            List<Placeable> list = new List<Placeable>();
            try {
                foreach (Placeable p in Placeable.AllPlaceables) {
                    if (p == null) continue;
                    // **顺带回收死条目**：方块被游戏自己销毁时（掉出界 / 被炸弹炸 / 关卡重置 ->
                    //  destroyMarkedPieces 直接 Object.Destroy，**不派发 DestroyPieceEvent**），
                    //  _placements 收不到通知，记录会一直留着，并且**强引用住已销毁的 Unity 对象**
                    //  （Unity 对象被引用着就无法 GC）-> 连续多回合不换场景时字典单调增长。
                    //  这里本来就在遍历 AllPlaceables，顺手清掉，零额外开销。
                    //  （在 foreach 里 Remove 是安全的：移除的是另一张字典，不是正在遍历的集合。）
                    if (p.MarkedForDestruction) { _placements.Remove(p); continue; }
                    if (ResolveInfo(p) == null) continue;   //关卡自带布局没有归属 -> 不算玩家放置
                    list.Add(p);
                }
            } catch (Exception __ex) { SR.Guard.Log("收集玩家放置方块", __ex); }
            return list;
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
