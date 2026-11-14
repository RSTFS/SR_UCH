// 混乱牌组（树屋选举版）+ 房内 mod 清单。
//
// 核心主张：发「真实 TabletRule + 真实 Value」，而不是 None 带外信号。
//   依据：TabletRulesScreen.handleEvent 的守卫是
//        `if (msg.NewRule != TabletRule.None && !LobbyManager.instance.IsHost)`  （反编译 111469）
//   即「非房主端收到真实规则值，由游戏自己的代码写进 Modifiers」。
//   所以没装 mod 的房客也能被房主带着变 —— 仅房主安装即可带全场。
//   代价：房主自己不执行那段（被 !IsHost 挡住），房主必须本地双写一次。
//   Value2 高位放牌组魔数，装了 mod 的房客据此把消息解析成「牌面」用于 UI；
//   游戏的这 10 个规则分支都只读 Value / Valueb，不读 Value2（111512-111613），
//   所以标记不会干扰原生规则，玩家在规则书里手动改的规则（Value2 = 0）也不会被误当成抽牌。
//
// 三条刻意收窄：
//   1) 射速牌（RateOfFire）已加回牌池；它会在游戏侧调 OnModifiersDynamicChange()，属于预期行为。
//      （111551），该函数会写 Time.timeScale = GameSpeed，与「时间控制」抢同一全局状态。
//   2) GameSpeed / CharacterSize / CameraFlip / Doomsday 不进池：前者归时间控制所有，
//      后三者碰相机或要重建绘制对象。
//   3) 还原「双写」：既重发每条规则的树屋原值（让没装 mod 的房客还原），
//      也发一条带外 Restore 消息（让装了 mod 的房客清掉牌面标记），
//      同时房主自己本地还原被 !IsHost 挡住的那份。
//
// 房内 mod 清单（握手）：复用 MsgGameRuleSet，以 TabletRule.None(0) 承载，
//   Value2 高位放魔数认领自己的消息。None 的三个游戏消费者全部短路：
//     111469 `NewRule != None` 不成立、106439 switch 无 None case、
//     TabletRuleUtility.IsRuleModifierChange(None) 返回 false。
//   Value 放本机 networkNumber（不用 conn —— 在客户端所有消息都来自服务器连接，无法区分发件人）。
//
// 不新增 Harmony 补丁、不新增 msgType、不新增反射登记：
//   自检基线 patches / reflection / api 计数不变，仅 features +1。
// 不碰相机、不生成实体、不加线程；状态只在主线程回调里读写。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;   // GameEventManager / IGameEventListener 都在 namespace GameEvent 内（反编译 151389）
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.chaos", "Chaos", "2.2")]
    public class Chaos : ITweak {
        private const string Sec = "混乱牌组";

        // 带外消息：Value2 = (Magic << 8) | op
        private const int Magic = 771;
        private const int OpHello = 1;
        private const int OpRestore = 2;
        // 真实规则消息：Value2 = (CardMagic << 16) | (op << 8) | cardId
        private const int CardMagic = 772;
        private const int OpCardDraw = 4;
        private const int OpCardRestore = 5;
        private const int OpClear = 6;          // 只清牌面，不动游戏规则（TabletRule.None）
        private const float RosterTtl = 60f;

        private struct CardDef {
            public string Zh, En;
            public TabletRule Rule;
            public int Field;
            public bool IsBool;
            public int Min, Max;
            public CardDef(string zh, string en, TabletRule rule, int field, bool isBool, int min, int max) {
                Zh = zh; En = en; Rule = rule; Field = field; IsBool = isBool; Min = min; Max = max;
            }
        }

        private const int FGravity = 0, FJump = 1, FSprint = 2, FPlatform = 3, FRate = 4;
        private static int _drawSeq;   // 抽牌序号：保证每次重抽的随机种子都不同（否则 TickCount 同毫秒会抽出同一张）
        private const int FBoolWallJumps = 8, FBoolDance = 9, FBoolMirror = 10, FBoolPlayerCol = 12, FBoolFriction = 13;

        // 17 张牌：5 个数值规则的多档取值（重力 / 跳跃 / 冲刺 / 平台 / 射速）+ 5 个布尔规则。
        private static readonly CardDef[] Cards = new CardDef[] {
            new CardDef("低重力",   "Low gravity",      TabletRule.ModifierGravity,            FGravity,       false, 1, 2),
            new CardDef("高重力",   "High gravity",     TabletRule.ModifierGravity,            FGravity,       false, 2, 2),
            new CardDef("弱跳跃",   "Weak jump",        TabletRule.ModifierJumpSpeed,          FJump,          false, 1, 3),
            new CardDef("慢冲刺",   "Slow sprint",      TabletRule.ModifierSprintSpeed,        FSprint,        false, 1, 4),
            new CardDef("慢平台",   "Slow platforms",   TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 1, 4),
            new CardDef("禁墙跳",   "No wall jumps",    TabletRule.ModifierWallJumpsDisabled,  FBoolWallJumps, true,  0, 0),
            new CardDef("镜像操作", "Mirror controls",  TabletRule.ModifierMirrorControls,     FBoolMirror,    true,  0, 0),
            new CardDef("跳舞无敌", "Dance invincible", TabletRule.ModifierDanceInvincibility, FBoolDance,     true,  0, 0),
            new CardDef("玩家互撞", "Player colliding", TabletRule.ModifierPlayerPlayerCollisions, FBoolPlayerCol, true, 0, 0),
            new CardDef("无摩擦",   "Frictionless",     TabletRule.ModifierFrictionless,       FBoolFriction,  true,  0, 0),
            new CardDef("慢射速",   "Slow fire rate",   TabletRule.ModifierRateOfFire,         FRate,          false, 1, 3),
            new CardDef("高跳跃",   "High jump",        TabletRule.ModifierJumpSpeed,          FJump,          false, 2, 3),
            new CardDef("快冲刺",   "Fast sprint",      TabletRule.ModifierSprintSpeed,        FSprint,        false, 2, 4),
            new CardDef("快平台",   "Fast platforms",   TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 2, 4),
            new CardDef("超重",     "Crushing gravity", TabletRule.ModifierGravity,            FGravity,       false, 3, 3),
            new CardDef("龟速冲刺", "Crawl sprint",     TabletRule.ModifierSprintSpeed,        FSprint,        false, 1, 1),
            new CardDef("停滞平台", "Frozen platforms", TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 1, 1),
        };

        private struct ActiveCard { public int Id, Param; public ActiveCard(int id, int param) { Id = id; Param = param; } }
        private struct Snapshot {
            public int Gravity, Jump, Sprint, Platform, Rate;
            public bool WallJumps, Dance, Mirror, PlayerCol, Friction;
        }

        private static ConfigEntry<bool> _enabled, _announce;
        private static ConfigEntry<int> _everyRounds, _maxActive;
        private static ConfigEntry<KeyCode> _drawKey;
        private static ConfigEntry<bool>[] _pool;

        private static ChaosListener _listener;
        private static readonly List<ActiveCard> _active = new List<ActiveCard>();
        private static Snapshot _snap;
        private static bool _hasSnapshot, _applied, _ready;
        private static int _roundSeen;
        private static float _lastPlaceAt = -100f;   // 上次进入摆放阶段的时刻（去重：刷新派对盒会重入 PLACE）
        private struct RosterEntry {
            public string Ver; public float At;
            public RosterEntry(string v, float a) { Ver = v; At = a; }
        }
        private static readonly Dictionary<int, RosterEntry> _roster = new Dictionary<int, RosterEntry>();
        private static bool _helloSent;
        private static float _nextHelloAt;

        // ============ 生命周期 ============

        private static bool _uiReg;

        // 配置行统一走"标签 + 控件"：RenderControl 只画控件、没有名称列，
        // 那样 LocKey / LocDesc 登记不会被消费，界面上也没有任何配置项名称。
        // 列宽与核心 Sc() 同坐标系；契约层未暴露 EntryNameWidth，故用固定 180 设计像素。
        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        // 本页配置行的显示名与悬浮说明：缺 LocKey 时中文界面回退英文键名，
        // 缺 LocDesc 时英文界面完全没有提示（内部条目不会回退 Bind 的中文描述）。
        private static void RegUi() {
            SRApi.LocKey(Sec, "Enabled", "启用混乱牌组", "Enable chaos deck");
            SRApi.LocDesc(Sec, "Enabled",
                "启用混乱牌组\n树屋里选牌组，每回合抽牌改变规则；关闭即还原。",
                "Enable chaos deck\nPick cards in the treehouse; each round changes a rule. Off restores everything.");
            SRApi.LocKey(Sec, "EveryNRounds", "抽牌间隔（回合）", "Draw every N rounds");
            SRApi.LocDesc(Sec, "EveryNRounds",
                "抽牌间隔（回合）\n几回合抽一次：1 = 每个摆放阶段都抽。",
                "Draw every N rounds\n1 = draw at every build phase. Range 1-5.");
            SRApi.LocKey(Sec, "MaxActive", "同时生效牌数", "Max active cards");
            SRApi.LocDesc(Sec, "MaxActive",
                "同时生效牌数\n最多同时生效几张牌。",
                "Max active cards\nHow many cards may apply at once. Range 1-4.");
            SRApi.LocKey(Sec, "Announce", "抽牌提示", "Announce draws");
            SRApi.LocDesc(Sec, "Announce",
                "抽牌提示\n抽牌时在屏幕上提示本回合牌面。",
                "Announce draws\nShow the drawn card on screen when a round starts.");
            SRApi.LocKey(Sec, "RerollKey", "重抽快捷键", "Reroll key");
            SRApi.LocDesc(Sec, "RerollKey",
                "重抽快捷键\n房主按此键立刻重抽一张。",
                "Reroll key\nHost: press to draw a new card immediately.");
            SRApi.LocKey(Sec, "Low gravity", "低重力", "Low gravity");
            SRApi.LocDesc(Sec, "Low gravity",
                "低重力\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Low gravity\nInclude the low gravity card in the draw pool.");
            SRApi.LocKey(Sec, "High gravity", "高重力", "High gravity");
            SRApi.LocDesc(Sec, "High gravity",
                "高重力\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "High gravity\nInclude the high gravity card in the draw pool.");
            SRApi.LocKey(Sec, "Weak jump", "弱跳跃", "Weak jump");
            SRApi.LocDesc(Sec, "Weak jump",
                "弱跳跃\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Weak jump\nInclude the weak jump card in the draw pool.");
            SRApi.LocKey(Sec, "Slow sprint", "慢冲刺", "Slow sprint");
            SRApi.LocDesc(Sec, "Slow sprint",
                "慢冲刺\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Slow sprint\nInclude the slow sprint card in the draw pool.");
            SRApi.LocKey(Sec, "Slow platforms", "慢平台", "Slow platforms");
            SRApi.LocDesc(Sec, "Slow platforms",
                "慢平台\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Slow platforms\nInclude the slow platforms card in the draw pool.");
            SRApi.LocKey(Sec, "No wall jumps", "禁墙跳", "No wall jumps");
            SRApi.LocDesc(Sec, "No wall jumps",
                "禁墙跳\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "No wall jumps\nInclude the no-wall-jumps card in the draw pool.");
            SRApi.LocKey(Sec, "Mirror controls", "镜像操作", "Mirror controls");
            SRApi.LocDesc(Sec, "Mirror controls",
                "镜像操作\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Mirror controls\nInclude the mirror controls card in the draw pool.");
            SRApi.LocKey(Sec, "Dance invincible", "跳舞无敌", "Dance invincible");
            SRApi.LocDesc(Sec, "Dance invincible",
                "跳舞无敌\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Dance invincible\nInclude the dance invincibility card in the draw pool.");
            SRApi.LocKey(Sec, "Player colliding", "玩家互撞", "Player colliding");
            SRApi.LocDesc(Sec, "Player colliding",
                "玩家互撞\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Player colliding\nInclude the player collisions card in the draw pool.");
            SRApi.LocKey(Sec, "Frictionless", "无摩擦", "Frictionless");
            SRApi.LocDesc(Sec, "Frictionless",
                "无摩擦\n是否把这张牌放进随机池\n关闭后本局不会抽到它",
                "Frictionless\nInclude the frictionless card in the draw pool.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "EveryNRounds": min = 1.0f; max = 5.0f; return true;
                    case "MaxActive": min = 1.0f; max = 4.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", false, "树屋里选牌组，每回合抽牌改变规则；关闭即还原。");
            _everyRounds = plugin.Config.Bind(Sec, "EveryNRounds", 1, new ConfigDescription("几回合抽一次：1 = 每个摆放阶段都抽。", new AcceptableValueRange<int>(1, 5)));
            _maxActive = plugin.Config.Bind(Sec, "MaxActive", 1, new ConfigDescription("最多同时生效几张牌。", new AcceptableValueRange<int>(1, 4)));
            _announce = plugin.Config.Bind(Sec, "Announce", true, "抽牌时在屏幕上提示本回合牌面。");
            _drawKey = plugin.Config.Bind(Sec, "RerollKey", KeyCode.None, "房主按此键立刻重抽一张。");

            _pool = new ConfigEntry<bool>[Cards.Length];
            for (int i = 0; i < Cards.Length; i++) {
                var c = Cards[i];
                _pool[i] = plugin.Config.Bind(Sec, c.En, true, "是否进入随机池：" + c.Zh);
                // 本地化跟着牌表走：以后加牌不用再记得补 LocKey（此前扩到 14 张只登记了 10 张）。
                SRApi.LocPair(Sec, c.En, c.Zh, c.En,
                              c.Zh + "\n勾选后才可能被抽到。", c.En + "\ncheck to include it in the draw pool.");
            }

            _enabled.SettingChanged += (s2, e2) => { if (!_enabled.Value) RestoreAll(); };

            SRApi.RegisterKey("混乱牌组-重抽", _drawKey, "press");

            // 对局播报：开局公告读本局启用的牌池（不是固定牌序，牌是逐回合随机抽的）。
            // 上一版漏了栏目登记：侧栏会显示英文 id 且排序靠后。补上中英名与顺序。
            SRApi.LocSec(Sec, "混乱牌组", "Chaos Deck");
            RegUi();
            SRApi.Nav(Sec, 130);
            // 每帧钩子：重抽键与握手都在 Tick 里。
            // 此前这里漏了注册，整个每帧调度线是死的（重抽键完全无效）。
            SRApi.RegisterTick("混乱牌组-每帧", Tick);
            SRApi.RegisterPage(Sec, "Deck", "牌组", "Deck", 118, RenderPage);
            SRApi.RegisterPage(Sec, "Pool", "牌池", "Card pool", 130, RenderPoolPage);
            SRApi.OnShutdown("混乱牌组", Shutdown);

            // 静态幂等：重复 Initialize 不能叠加监听器（关机回调按名字覆盖，天然幂等）。
            if (_listener == null) {
                _listener = new ChaosListener(this);
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, true);
            }
            _ready = true;
        }

        // 关机/卸载回调（经 SRApi.OnShutdown 注册）：解监听、撤规则。
        public void Shutdown() {
            if (_listener != null) {
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, false);
                _listener = null;
            }
            RestoreAll();
            _ready = false;
        }

        public void Tick() {
            if (!_ready) return;
            if (_enabled.Value == false) return;                       // 总开关关掉后不跑任何每帧逻辑
            // 用项目的组合键框架：Shift/Ctrl 组合能用，且聊天框打字时让路（裸 Input.GetKeyDown 会误触发）。
            if (IsAuthority && SRApi.ComboKeyDown(_drawKey)) DrawAndBroadcast();
            SendHelloOnce();
        }

        private class ChaosListener : GameEvent.IGameEventListener {
            private readonly Chaos _o;
            public ChaosListener(Chaos o) { _o = o; }
            public void handleEvent(GameEvent.GameEvent e) {
                if (e is GameEvent.StartPhaseEvent) { _o.OnStartPhase((GameEvent.StartPhaseEvent)e); return; }
                if (e is GameEvent.GameEndEvent) { _o.OnGameEnd(); return; }
                if (e is GameEvent.NewMatchEvent) { _o.OnNewMatch(); return; }
                if (e is GameEvent.NetworkMessageReceivedEvent) { _o.OnNet((GameEvent.NetworkMessageReceivedEvent)e); return; }
            }
        }

        // ============ 环境判定 ============

        private static LobbyManager SafeLobby() { try { return LobbyManager.instance; } catch { return null; } }
        private static bool NetServerActive() { try { return NetworkServer.active; } catch { return false; } }
        private static bool NetClientActive() { try { return NetworkClient.active; } catch { return false; } }
        private static Modifiers SafeMods() { try { return Modifiers.GetInstance(); } catch { return null; } }
        private static bool IsOnline() { var lm = SafeLobby(); return lm != null && lm.IsInOnlineGame; }
        private static bool IsHostSelf() { var lm = SafeLobby(); return lm != null && lm.IsHost; }

        private static bool IsAuthority {
            get {
                var lm = SafeLobby();
                if (lm == null) return true;
                if (!lm.IsInOnlineGame) return true;
                return lm.IsHost && NetServerActive();
            }
        }

        private static int LocalNetworkNumber() {
            try {
                if (LobbyManager.instance == null) return -1;
                NetworkLobbyPlayer[] slots = LobbyManager.instance.lobbySlots;
                if (slots == null) return -1;
                for (int i = 0; i < slots.Length; i++) {
                    LobbyPlayer lp = slots[i] as LobbyPlayer;
                    if (lp != null && lp.LocalPlayer != null) return lp.networkNumber;
                }
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/本机玩家号", ex); }
            return -1;
        }

        // ============ 阶段边界 ============

        private void OnStartPhase(GameEvent.StartPhaseEvent e) {
            if (!_ready || !_enabled.Value) return;
            if (e == null || e.Phase != GameControl.GamePhase.PLACE) return;

            // 房客端绝不本地重建：规则由房主广播 + 游戏写入 Modifiers。
            // 若在这里 RebuildLocal()，会用「已被房主改过的值」Capture 快照，
            // 再用空的 _active 还原，把房主的牌冲掉 —— 联机规则不同步的老坑。
            if (!IsAuthority) return;
            // 刷新派对盒 / 重载关卡会再次广播 PLACE 阶段，但那是同一回合：
            // 3 秒内重复进入按一次算，否则"刷新一下盒子就自动重抽"。
            float now = Time.unscaledTime;
            if (now - _lastPlaceAt < 3f) return;
            _lastPlaceAt = now;
            _roundSeen++;
            if (_roundSeen % Mathf.Max(1, _everyRounds.Value) == 0) DrawAndBroadcast();
            else if (_applied) RebuildLocal();
        }

        private void OnGameEnd() { RestoreAll(); }

        /// <summary>换局：只清本机状态，不广播（房主还没抽牌，游戏自己会重置规则）。</summary>
        private void OnNewMatch() {
            _active.Clear();
            _applied = false;
            _hasSnapshot = false;
            _roundSeen = 0;
            _roster.Clear();
        }

        // ============ 抽牌与广播 ============

        private void DrawAndBroadcast() {
            var chosen = new List<int>();
            for (int i = 0; i < Cards.Length; i++) if (_pool[i] != null && _pool[i].Value) chosen.Add(i);
            if (chosen.Count == 0) return;

            // 关键：先把上一回合生效过的规则按树屋原值重发。
            // 房主本地 RebuildLocal 会「整体还原再重建」，但没装 mod 的房客只收到
            // 新牌那条规则消息 —— 不发还原，房客的规则会逐回合累积，与房主不一致。
            BroadcastRestoreRules();
            if (NetClientActive()) SendSignal(OpClear);

            _drawSeq++;
            var rng = new System.Random(Environment.TickCount ^ (_roundSeen * 7919) ^ (_drawSeq * 104729));
            int want = Mathf.Clamp(_maxActive.Value, 1, 4);
            _active.Clear();
            for (int i = 0; i < want; i++) {
                int id = chosen[rng.Next(chosen.Count)];
                int param = ParamFor(id, rng);
                _active.Add(new ActiveCard(id, param));
                BroadcastOne(id, param);
            }
            RebuildLocal();
            string drawn = Desc() ?? "-";
            if (_announce.Value) SRApi.NotifyT("混乱牌组：{0}", "Chaos: {0}", drawn);
        }

        private int ParamFor(int id, System.Random rng) {
            var c = Cards[id];
            if (c.IsBool) return 1;
            int lo = Mathf.Max(1, c.Min), hi = c.Max;
            var m = SafeMods();
            if (m != null) {
                int arrLen = ArrayLengthFor(c.Field, m);
                if (arrLen > 0) hi = Mathf.Min(hi, arrLen - 1);
            }
            if (hi < lo) hi = lo;
            return lo + rng.Next(hi - lo + 1);
        }

        private static int ArrayLengthFor(int field, Modifiers m) {
            switch (field) {
                case FGravity:  return m.GravityValues != null ? m.GravityValues.Length : 0;
                case FRate:     return m.RateOfFireValues != null ? m.RateOfFireValues.Length : 0;
                case FJump:     return m.JumpSpeedValues != null ? m.JumpSpeedValues.Length : 0;
                case FSprint:   return m.SprintModifiers != null ? m.SprintModifiers.Length : 0;
                case FPlatform: return m.PlatformMoveSpeedValues != null ? m.PlatformMoveSpeedValues.Length : 0;
            }
            return 0;
        }

        /// <summary>发一条真实规则消息。房客由游戏自己写 Modifiers；房主被 !IsHost 挡住，靠本地双写。</summary>
        /// <remarks>方法定义在 TabletModalOverlay（:108555），不是 TabletRulesScreen —— 后者只有调用点。</remarks>
        private static void BroadcastOne(int cardId, int param, bool restore = false) {
            if (!NetClientActive()) return;
            CardDef c = Cards[cardId];
            try {
                int v2 = (CardMagic << 16) | ((restore ? OpCardRestore : OpCardDraw) << 8) | (cardId & 0xFF);
                if (c.IsBool) TabletModalOverlay.BroadcastRuleChange(c.Rule, 0, v2, param != 0);
                else TabletModalOverlay.BroadcastRuleChange(c.Rule, param, v2, false);
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/广播", ex); }
        }

        // ============ 本地落位（幂等重建：先还原再按序重建） ============

        private void RebuildLocal() {
            // 只有权威端（房主 / 离线本机）能写本地 Modifiers；房客端规则由游戏写入。
            if (!IsAuthority) return;
            var m = SafeMods();
            if (m == null) return;
            if (!_hasSnapshot) { Capture(m); _hasSnapshot = true; }
            RestoreLocal(m);
            for (int i = 0; i < _active.Count; i++) ApplyLocal(m, Cards[_active[i].Id], _active[i].Param);
            _applied = true;
        }

        /// <summary>把当前生效牌对应的规则按树屋原值重发，让没装 mod 的房客回到基线。</summary>
        private static void BroadcastRestoreRules() {
            if (!IsAuthority || !NetClientActive() || !_hasSnapshot) return;
            for (int i = 0; i < _active.Count; i++) {
                int id = _active[i].Id;
                int orig = SnapshotValue(Cards[id].Field);
                BroadcastOne(id, Cards[id].IsBool ? (orig != 0 ? 1 : 0) : orig, true);
            }
        }

        private void RestoreAll() {
            // 1) 没装 mod 的房客：重发每条曾生效规则的树屋原值，让游戏自己写回。
            if (_hasSnapshot && _active.Count > 0 && IsAuthority) {
                BroadcastRestoreRules();
                // 2) 装了 mod 的房客：清掉牌面标记，避免下一局显示过期牌。
                SendSignal(OpRestore);
            }
            // 3) 房主自己（被 !IsHost 挡住）本地还原；房客端不碰 Modifiers。
            if (_hasSnapshot && IsAuthority) { var m = SafeMods(); if (m != null) RestoreLocal(m); }
            _active.Clear();
            _applied = false;
            _hasSnapshot = false;
            _roundSeen = 0;
        }

        private static int SnapshotValue(int field) {
            switch (field) {
                case FGravity:  return _snap.Gravity;
                case FRate:     return _snap.Rate;
                case FJump:     return _snap.Jump;
                case FSprint:   return _snap.Sprint;
                case FPlatform: return _snap.Platform;
                case FBoolWallJumps: return _snap.WallJumps ? 1 : 0;
                case FBoolDance:     return _snap.Dance ? 1 : 0;
                case FBoolMirror:    return _snap.Mirror ? 1 : 0;
                case FBoolPlayerCol: return _snap.PlayerCol ? 1 : 0;
                case FBoolFriction:  return _snap.Friction ? 1 : 0;
            }
            return 0;
        }

        private void Capture(Modifiers m) {
            _snap.Gravity = m.GravityMode;
            _snap.Jump = m.JumpSpeedMode;
            _snap.Sprint = m.SprintSpeedMode;
            _snap.Platform = m.PlatformSpeedMode;
            _snap.Rate = m.RateOfFireMode;
            _snap.WallJumps = m.wallJumpsDisabled;
            _snap.Dance = m.danceInvincibility;
            _snap.Mirror = m.mirrorControls;
            _snap.PlayerCol = m.playerPlayerCollisions;
            _snap.Friction = m.frictionless;
        }

        private void RestoreLocal(Modifiers m) {
            m.GravityMode = _snap.Gravity;
            m.JumpSpeedMode = _snap.Jump;
            m.SprintSpeedMode = _snap.Sprint;
            m.PlatformSpeedMode = _snap.Platform;
            m.RateOfFireMode = _snap.Rate;
            m.wallJumpsDisabled = _snap.WallJumps;
            m.danceInvincibility = _snap.Dance;
            m.mirrorControls = _snap.Mirror;
            m.playerPlayerCollisions = _snap.PlayerCol;
            m.frictionless = _snap.Friction;
        }

        private static void ApplyLocal(Modifiers m, CardDef c, int param) {
            switch (c.Field) {
                case FGravity:  m.GravityMode = param; break;
                case FRate:     m.RateOfFireMode = param; break;
                case FJump:     m.JumpSpeedMode = param; break;
                case FSprint:   m.SprintSpeedMode = param; break;
                case FPlatform: m.PlatformSpeedMode = param; break;
                case FBoolWallJumps: m.wallJumpsDisabled = param != 0; break;
                case FBoolDance:     m.danceInvincibility = param != 0; break;
                case FBoolMirror:    m.mirrorControls = param != 0; break;
                case FBoolPlayerCol: m.playerPlayerCollisions = param != 0; break;
                case FBoolFriction:  m.frictionless = param != 0; break;
            }
        }

        // ============ 房内 mod 清单 ============

        private void SendHelloOnce() {
            if (!_ready || !IsOnline() || !NetClientActive()) return;
            if (_helloSent && Time.unscaledTime < _nextHelloAt) return;
            _helloSent = true;
            _nextHelloAt = Time.unscaledTime + 8f;   // 周期性重发，覆盖晚加入的房客
            // 名单带时间戳：退出房间 / 换局后旧条目 60 秒过期，不会一直显示幽灵端。
            float now = Time.unscaledTime;
            var dead = new List<int>();
            foreach (var kv in _roster) if (now - kv.Value.At > RosterTtl) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _roster.Remove(dead[i]);
            SendSignal(OpHello);
        }

        private static void SendSignal(int op) {
            if (!NetClientActive()) return;   // 离线 / 本地派对没有客户端连接，别刷异常日志
            try {
                var msg = new MsgGameRuleSet();
                msg.NewRule = TabletRule.None;
                msg.Value = LocalNetworkNumber();
                msg.Value2 = (Magic << 8) | op;
                msg.Valueb = false;
                LobbyManager.instance.client.Send(NetMsgTypes.GameRuleSet, msg);
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/信号", ex); }
        }

        private void OnNet(GameEvent.NetworkMessageReceivedEvent e) {
            if (!_ready || e == null || e.Message == null) return;
            if (e.Message.msgType != NetMsgTypes.GameRuleSet) return;
            var m = e.ReadMessage as MsgGameRuleSet;
            if (m == null) return;

            // 自己的带外信号（None + 魔数）。
            if (m.NewRule == TabletRule.None && (m.Value2 >> 8) == Magic) {
                int op = m.Value2 & 0xFF;
                if (op == OpHello && m.Value >= 0 && m.Value != LocalNetworkNumber())
                    _roster[m.Value] = new RosterEntry("2.2", Time.unscaledTime);
                
                return;
            }

            // 只认「房主发的、带牌组魔数」的真实规则消息。
            // 玩家在规则书里手动改规则时 Value2 = 0，其它功能发的带外消息魔数也不同
            // —— 都不能被当成抽牌（否则会把牌面搞乱）。
            if (!IsOnline() || IsHostSelf() || !_enabled.Value) return;
            if ((m.Value2 >> 16) != CardMagic) return;
            int op2 = (m.Value2 >> 8) & 0xFF;
            int card = m.Value2 & 0xFF;
            if (card < 0 || card >= Cards.Length) return;
            if (op2 == OpCardRestore) { _active.Clear(); _applied = false; return; }
            if (op2 != OpCardDraw) return;
            // 同规则多张牌（低/高重力）只保留最新的一张，避免牌面同时出现两张。
            for (int i = _active.Count - 1; i >= 0; i--)
                if (Cards[_active[i].Id].Rule == Cards[card].Rule) _active.RemoveAt(i);
            int param = Cards[card].IsBool ? (m.Valueb ? 1 : 0) : m.Value;
            _active.Add(new ActiveCard(card, param));
            _applied = true;
        }

        // ============ 对外展示 ============

        public static string Desc() {
            // 同帧内 UI 与通知各调一次；用核心的按帧缓存只算一遍（帧号变了自动重算）。
            return SRApi.Cache.FrameSig("chaos.desc", delegate { return DescCore(); });
        }

        private static string DescCore() {
            if (!_ready || _active.Count == 0) return null;
            var parts = new List<string>();
            for (int i = 0; i < _active.Count; i++) {
                var c = Cards[_active[i].Id];
                // 播报要发给房客，牌面必须跟随房主界面语言；简报同样受益。
                string name = SRApi.T(c.Zh, c.En);
                parts.Add(c.IsBool ? name : name + " " + _active[i].Param);
            }
            return string.Join(" + ", parts.ToArray());
        }

        public static string RosterText() {
            if (!IsOnline()) return null;
            float now = Time.unscaledTime;
            int me = LocalNetworkNumber();
            var parts = new List<string>();
            foreach (var kv in _roster) {
                if (kv.Key == me || now - kv.Value.At > RosterTtl) continue;
                parts.Add("#" + kv.Key + " " + kv.Value.Ver);
            }
            string tail = parts.Count > 0
                ? string.Join(SRApi.T("、", ", "), parts.ToArray())
                : SRApi.T("尚未收到其他端回应", "no other clients replied yet");
            return SRApi.T("房内：本机已装 / 其他端 ", "Room: this machine installed / others ") + tail;
        }

        // ============ 树屋选牌页 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            // 配置项一律走核心统一渲染；核心层没有裸 Toggle/Slider/Label 控件。
            CfgRow(_enabled);
            CfgRow(_everyRounds);
            CfgRow(_maxActive);
            CfgRow(_announce);

            Line("当前牌面：" + (Desc() ?? "（未生效）"), "Active: " + (Desc() ?? "(none)"));
            Line(RosterText() ?? "房内 mod 清单：仅本机（离线）", RosterText() ?? "Room roster: this machine only (offline)");

            Line("牌池在「牌池」页勾选（勾上才可能被抽到）。", "Pick the drawable cards on the Card pool page.");

            Line("说明：仅房主需要安装；房客未安装时由游戏原生规则生效，房客能看到规则变化但看不到本页。",
                 "Only the host needs the mod. Guests without it still get the vanilla rule change, but cannot see this page.");
            Line("混乱牌组用规则消息的高位魔数认领自己的消息，其它功能与手动改规则都不会被误解析。",
                 "Chaos Deck claims its rule messages with a high-bit magic, so other features and manual rule edits are never misread.");
            Line("每次抽牌前会先把上一回合的规则按原值重发，房客的规则不会逐回合累积。",
                 "Before every draw the previous rules are re-broadcast at their original values, so guest rules never accumulate round over round.");
            SRApi.Ctl.HotkeyButton("chaos.reroll", SRApi.T("重抽", "Draw"),
                                   SRApi.T("仅房主 / 离线有效", "Host or offline only"), DrawAndBroadcast);
        }

        // 牌池单独一页：勾选行 + 批量操作（全选 / 全不选 / 只留减益 / 随机一半）。
        private void RenderPoolPage() {
            Line("勾上的牌才可能被抽到；至少留一张。", "Checked cards can be drawn; keep at least one enabled.");
            if (SRApi.Ctl.Button(SRApi.T("全选", "All"), SRApi.T("勾选全部牌", "Enable every card")))
                SetAllPools(true);
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("全不选", "None"), SRApi.T("全部取消（会再自动勾回第一张）", "Disable all (the first card is re-enabled)")))
                SetAllPools(false);
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("只留减益", "Debuffs"), SRApi.T("只保留让人变弱的牌（低重力/慢速/禁墙跳等）", "Keep only weakening cards")))
                KeepWeakening();
            GUILayout.Space(SRApi.Ctl.Sc(6f));
            for (int i = 0; i < Cards.Length; i++) CfgRow(_pool[i]);
        }

        private void SetAllPools(bool on) {
            try {
                for (int i = 0; i < _pool.Length; i++) {
                    if (_pool[i] == null) continue;
                    _pool[i].Value = on;
                }
                if (!on && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;   // 至少留一张
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/批量勾选", ex); }
        }

        private void KeepWeakening() {
            try {
                for (int i = 0; i < Cards.Length; i++) {
                    if (_pool[i] == null) continue;
                    CardDef c = Cards[i];
                    // 减益判定：低重力/慢冲刺/慢平台/慢射速/禁墙跳/无摩擦/超重（字段+参数方向）
                    bool weaken =
                        (c.Field == FGravity && c.Min > 1) ? false :           // 高重力让人更沉，先按非减益处理
                        (c.Field == FGravity) ||
                        (c.Field == FSprint && c.Min <= 2) ||
                        (c.Field == FPlatform && c.Min <= 2) ||
                        (c.Field == FRate) ||
                        c.Field == FBoolWallJumps || c.Field == FBoolFriction;
                    _pool[i].Value = weaken;
                }
                bool any = false;
                for (int i = 0; i < _pool.Length; i++) if (_pool[i] != null && _pool[i].Value) { any = true; break; }
                if (!any && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/只留减益", ex); }
        }
    }
}
