// 混乱牌组：树屋里选牌组，每回合抽牌改规则；仅房主安装即可带全场。
//
// 发「真实 TabletRule + 真实 Value」而非 None 带外信号：游戏只对「非房主 + 非 None」的规则消息写
// Modifiers；房主被 !IsHost 挡住，需本地双写一次。
//
// 注意事项：
// 1) Value2 高位放牌组魔数；游戏只读 Value / Valueb，手动改规则的 Value2 = 0 不会误判。
// 2) GameSpeed / RateOfFire / CharacterSize / CameraFlip / Doomsday 不进池：碰全局时间尺度、
//    相机或需要重建绘制对象。
// 3) 关闭开关向全场广播清零：真实规则消息覆盖未装 mod 的房客，带外信号让装 mod 的端全字段归零。
// 4) 房内清单复用 MsgGameRuleSet 以 None 承载，Value 放本机 networkNumber。
// 5) 不新增 Harmony 补丁 / msgType / 反射登记；状态只在主线程读写。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;   // GameEventManager / IGameEventListener 都在 namespace GameEvent 内（反编译 151389）
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.chaos", "Chaos", "2.2", Modes = (int)(SRApi.ModeMask.Party | SRApi.ModeMask.Creative))]
    public partial class Chaos : ITweak {
        private const string Sec = "混乱牌组";

        // 带外消息：Value2 = (Magic << 8) | op
        private const int Magic = 771;
        private const int OpHello = 1;
        private const int OpClearAll = 2;
        // 真实规则消息：Value2 = (CardMagic << 16) | (op << 8) | cardId
        private const int CardMagic = 772;
        private const int OpCardDraw = 4;
        private const int OpCardRestore = 5;
        private const float RosterTtl = 60f;

        private struct CardDef {
            public string Zh, En;
            public string TipZh, TipEn;
            public TabletRule Rule;
            public int Field;
            public bool IsBool;
            public int Min, Max;
            public CardDef(string zh, string en, string tipZh, string tipEn, TabletRule rule, int field, bool isBool, int min, int max) {
                Zh = zh; En = en; TipZh = tipZh; TipEn = tipEn; Rule = rule; Field = field; IsBool = isBool; Min = min; Max = max;
            }
        }

        private const int FGravity = 0, FJump = 1, FSprint = 2, FPlatform = 3;
        private static int _drawSeq;   // 抽牌序号：保证每次重抽的随机种子都不同（否则 TickCount 同毫秒会抽出同一张）
        private const int FBoolWallJumps = 8, FBoolDance = 9, FBoolMirror = 10, FBoolPlayerCol = 12, FBoolFriction = 13;

        // 15 张牌：4 个数值规则的多档取值 + 5 个布尔规则；每张带一句效果说明。
        private static readonly CardDef[] Cards = new CardDef[] {
            new CardDef("低重力",   "Low gravity",      "重力变轻，跳得更高",       "Lighter gravity, floatier jumps",      TabletRule.ModifierGravity,            FGravity,       false, 1, 2),
            new CardDef("高重力",   "High gravity",     "重力加重，下落更快",       "Heavier gravity, faster falls",        TabletRule.ModifierGravity,            FGravity,       false, 2, 2),
            new CardDef("弱跳跃",   "Weak jump",        "跳跃高度降低",             "Lower jumps",                          TabletRule.ModifierJumpSpeed,          FJump,          false, 1, 3),
            new CardDef("慢冲刺",   "Slow sprint",      "冲刺速度变慢",             "Slower sprinting",                     TabletRule.ModifierSprintSpeed,        FSprint,        false, 1, 4),
            new CardDef("慢平台",   "Slow platforms",   "平台移动变慢",             "Platforms move slower",                TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 1, 4),
            new CardDef("禁墙跳",   "No wall jumps",    "不能蹬墙跳",               "Wall jumps disabled",                  TabletRule.ModifierWallJumpsDisabled,  FBoolWallJumps, true,  0, 0),
            new CardDef("镜像操作", "Mirror controls",  "左右操作反转",             "Left/right reversed",                  TabletRule.ModifierMirrorControls,     FBoolMirror,    true,  0, 0),
            new CardDef("跳舞无敌", "Dance invincible", "跳舞时无敌",               "Invincible while dancing",             TabletRule.ModifierDanceInvincibility, FBoolDance,     true,  0, 0),
            new CardDef("玩家互撞", "Player colliding", "玩家之间会碰撞",           "Players collide with each other",      TabletRule.ModifierPlayerPlayerCollisions, FBoolPlayerCol, true, 0, 0),
            new CardDef("无摩擦",   "Frictionless",     "地面打滑，难以减速",       "Slippery, hard to stop",               TabletRule.ModifierFrictionless,       FBoolFriction,  true,  0, 0),
            new CardDef("高跳跃",   "High jump",        "跳跃高度提升",             "Higher jumps",                         TabletRule.ModifierJumpSpeed,          FJump,          false, 2, 3),
            new CardDef("快冲刺",   "Fast sprint",      "冲刺速度提升",             "Faster sprinting",                     TabletRule.ModifierSprintSpeed,        FSprint,        false, 2, 4),
            new CardDef("快平台",   "Fast platforms",   "平台移动变快",             "Platforms move faster",                TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 2, 4),
            new CardDef("龟速冲刺", "Crawl sprint",     "冲刺慢到接近走路",         "Sprint barely faster than walking",    TabletRule.ModifierSprintSpeed,        FSprint,        false, 1, 1),
            new CardDef("停滞平台", "Frozen platforms", "平台几乎不动",             "Platforms barely move",                TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 1, 1),
        };

        private struct ActiveCard { public int Id, Param; public ActiveCard(int id, int param) { Id = id; Param = param; } }
        private static ConfigEntry<bool> _enabled, _announce;
        private static ConfigEntry<int> _everyRounds, _maxActive;
        private static ConfigEntry<KeyCode> _drawKey;
        private static ConfigEntry<bool>[] _pool;

        private static ChaosListener _listener;
        private static readonly List<ActiveCard> _active = new List<ActiveCard>();
        private static bool _applied, _ready;
        private static int _roundSeen;
        // 真回合：与 HUD 共用 Pure/RoundTruth.cs。重载关卡 / 刷新派对盒会额外 +1 且没有结算，
        // 那种 StartPhaseEvent(PLACE) 不算新回合，不抽牌、也不重建规则。
        private static SR_UCH.Pure.RoundTruthState _rs;
        private static int _roundAdvancePending;
        private struct RosterEntry {
            public float At;
            public RosterEntry(float a) { At = a; }
        }
        private static readonly Dictionary<int, RosterEntry> _roster = new Dictionary<int, RosterEntry>();
        private static bool _helloSent;
        private static float _nextHelloAt;

        // ============ 生命周期 ============

        private static bool _uiReg;

        // 配置行走"标签 + 控件"，否则 LocKey / LocDesc 不会被消费；列宽固定 180 设计像素。

        // 显示名与悬浮说明必须登记。
        private static void RegUi() {
            TweakCfg.Entry(Sec, "Enabled", "启用混乱牌组", "Enable chaos deck",
                "启用混乱牌组\n树屋里选牌组，每回合抽牌改变规则；关闭即还原。", "Enable chaos deck\nPick cards in the treehouse; each round changes a rule. Off restores everything.");
            TweakCfg.Entry(Sec, "EveryNRounds", "抽牌间隔（回合）", "Draw every N rounds",
                "抽牌间隔（回合）\n几回合抽一次：1 = 每个真回合都抽\n只有结算推进才计数，重载 / 刷新派对盒不抽",
                "Draw every N rounds\n1 = draw every real round\nonly tally-confirmed advances count; reload / party-box refresh does not");
            TweakCfg.Entry(Sec, "MaxActive", "同时生效牌数", "Max active cards",
                "同时生效牌数\n最多同时生效几张牌。", "Max active cards\nHow many cards may apply at once. Range 1-4.");
            TweakCfg.Entry(Sec, "Announce", "抽牌提示", "Announce draws",
                "抽牌提示\n抽牌时在屏幕上提示本回合牌面。", "Announce draws\nShow the drawn card on screen when a round starts.");
            TweakCfg.Entry(Sec, "RerollKey", "重抽快捷键", "Reroll key",
                "重抽快捷键\n房主按此键立刻重抽一张。", "Reroll key\nHost: press to draw a new card immediately.");
            TweakCfg.Entry(Sec, "Low gravity", "低重力", "Low gravity",
                "低重力\n勾选后本局才可能抽到这张牌", "Low gravity\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "High gravity", "高重力", "High gravity",
                "高重力\n勾选后本局才可能抽到这张牌", "High gravity\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Weak jump", "弱跳跃", "Weak jump",
                "弱跳跃\n勾选后本局才可能抽到这张牌", "Weak jump\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Slow sprint", "慢冲刺", "Slow sprint",
                "慢冲刺\n勾选后本局才可能抽到这张牌", "Slow sprint\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Slow platforms", "慢平台", "Slow platforms",
                "慢平台\n勾选后本局才可能抽到这张牌", "Slow platforms\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "No wall jumps", "禁墙跳", "No wall jumps",
                "禁墙跳\n勾选后本局才可能抽到这张牌", "No wall jumps\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Mirror controls", "镜像操作", "Mirror controls",
                "镜像操作\n勾选后本局才可能抽到这张牌", "Mirror controls\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Dance invincible", "跳舞无敌", "Dance invincible",
                "跳舞无敌\n勾选后本局才可能抽到这张牌", "Dance invincible\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Player colliding", "玩家互撞", "Player colliding",
                "玩家互撞\n勾选后本局才可能抽到这张牌", "Player colliding\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Frictionless", "无摩擦", "Frictionless",
                "无摩擦\n勾选后本局才可能抽到这张牌", "Frictionless\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "High jump", "高跳跃", "High jump",
                "高跳跃\n勾选后本局才可能抽到这张牌", "High jump\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Fast sprint", "快冲刺", "Fast sprint",
                "快冲刺\n勾选后本局才可能抽到这张牌", "Fast sprint\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Fast platforms", "快平台", "Fast platforms",
                "快平台\n勾选后本局才可能抽到这张牌", "Fast platforms\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Crawl sprint", "龟速冲刺", "Crawl sprint",
                "龟速冲刺\n勾选后本局才可能抽到这张牌", "Crawl sprint\nChecked cards can be drawn this match");
            TweakCfg.Entry(Sec, "Frozen platforms", "停滞平台", "Frozen platforms",
                "停滞平台\n勾选后本局才可能抽到这张牌", "Frozen platforms\nChecked cards can be drawn this match");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        
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
            _everyRounds = plugin.Config.Bind(Sec, "EveryNRounds", 1, new ConfigDescription("几回合抽一次：1 = 每个真回合都抽（只有结算推进才计数）。", new AcceptableValueRange<int>(1, 5)));
            _maxActive = plugin.Config.Bind(Sec, "MaxActive", 1, new ConfigDescription("最多同时生效几张牌。", new AcceptableValueRange<int>(1, 4)));
            _announce = plugin.Config.Bind(Sec, "Announce", true, "抽牌时在屏幕上提示本回合牌面。");
            _drawKey = plugin.Config.Bind(Sec, "RerollKey", KeyCode.None, "房主按此键立刻重抽一张。");

            _pool = new ConfigEntry<bool>[Cards.Length];
            for (int i = 0; i < Cards.Length; i++) {
                var c = Cards[i];
                _pool[i] = plugin.Config.Bind(Sec, c.En, true, "是否进入随机池：" + c.Zh);
            }

            // 本功能配置不存盘：启动时强制回到默认值。
            try {
                _enabled.Value = false;
                _everyRounds.Value = 1;
                _maxActive.Value = 1;
                _announce.Value = true;
                _drawKey.Value = KeyCode.None;
                for (int __i = 0; __i < _pool.Length; __i++) {
                    if (_pool[__i] != null) _pool[__i].Value = true;
                }
            } catch (Exception __rs) { SRApi.Guard.Log("混乱牌组/会话重置", __rs); }

            // 总开关关闭：广播全场清零，本机与所有房客落到同一终态。
            System.EventHandler __h__enabled_1 = (s2, e2) => { if (!_enabled.Value) { ClearAll(true); } };
            _enabled.SettingChanged += __h__enabled_1;
            Sub.Revoke(() => _enabled.SettingChanged -= __h__enabled_1);

            SRApi.NetMagic.Register("chaos.signal", Magic);
            SRApi.NetMagic.Register("chaos.card", CardMagic);
            // 模式限制由核心统一判定（见 SR.RegisterKey 的 impl 重载），这里只判房主与开关。
            SRApi.RegisterKey("混乱牌组-重抽", _drawKey, "press", delegate {
                if (!IsAuthority) return;
                if (_enabled != null && _enabled.Value) DrawAndBroadcast();
            }, typeof(Chaos));

            // 播报读本局启用的牌池（牌逐回合随机抽）。
            SRApi.LocSec(Sec, "混乱牌组", "Chaos Deck");
            RegUi();
            SRApi.Nav(Sec, 130);
            // 每帧钩子：重抽键与握手都在 Tick 里。
            SRApi.RegisterTick("混乱牌组-每帧", Tick);
            SRApi.RegisterPage(Sec, "Deck", "牌组", "Deck", 118, RenderPage);
            SRApi.RegisterPage(Sec, "Pool", "牌池", "Card pool", 130, RenderPoolPage);
            SRApi.OnShutdown("混乱牌组", Shutdown);

            
            if (_listener == null) {
                _listener = new ChaosListener(this);
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, true);
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, true);
            }
            _ready = true;
        }

        
        public void Shutdown() {
            if (_listener != null) {
                GameEventManager.ChangeListener<GameEvent.StartPhaseEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.GameEndEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NewMatchEvent>(_listener, false);
                GameEventManager.ChangeListener<GameEvent.NetworkMessageReceivedEvent>(_listener, false);
                _listener = null;
            }
            ClearLocal();
            _ready = false;
        }

        public void Tick() {
            if (!_ready) return;
            if (_enabled.Value == false) return;                       // 总开关关掉后不跑任何每帧逻辑
            if (!ModeAllowed()) return;                                 // 限制模式：静默跳过
            PumpRoundTruth();
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
        // 只在派对 / 创意模式生效，其它模式静默跳过。
        private static bool ModeAllowed() {
            try {
                
                if (SRApi.Env.InTreehouse) return true;
                return SRApi.FeatureAllowed(typeof(Chaos));
            }
            catch (Exception __ex) { SRApi.Guard.Log("混乱牌组/模式判定", __ex); return false; }
        }
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

        // ============ 阶段边界 / 真回合 ============

        // 每帧读游戏回合数，交给共享状态机判定「真回合」是否推进。
        private static void PumpRoundTruth() {
            try {
                LobbyManager lm = SafeLobby();
                GameControl gc = (lm != null) ? lm.CurrentGameController : null;
                if (gc == null) return;
                if (SR_UCH.Pure.RoundTruth.Step(ref _rs, gc.RoundNumber, false)) _roundAdvancePending++;
            } catch (Exception __rt) { SRApi.Guard.Log("混乱牌组/真回合判定", __rt); }
        }

        private void OnStartPhase(GameEvent.StartPhaseEvent e) {
            if (!_ready || !_enabled.Value) return;
            if (e == null || e.Phase != GameControl.GamePhase.PLACE) return;

            // 抽牌时机仍是建造阶段开始，但只有真回合推进才计数：
            // 重载关卡 / 刷新派对盒会直接调 ToPlaceMode() 发 PLACE 且把 RoundNumber +1，
            // 却没有结算记分板，那种 PLACE 一律不抽牌、不重建。
            // 房客端同步消费计数（不抽牌），避免两端状态分叉。
            if (_roundAdvancePending <= 0) return;
            _roundAdvancePending--;
            // 房客端绝不本地重建：只跟踪房主广播的牌面，避免用本机脏值覆盖权威状态。
            if (!IsAuthority) return;
            _roundSeen++;
            if (_roundSeen % Mathf.Max(1, _everyRounds.Value) == 0) DrawAndBroadcast();
            else if (_applied) RebuildLocal();
        }

        private void OnGameEnd() { ClearLocal(); }

        /// <summary>换局：只清本机状态，不广播（游戏自己会重置规则）。</summary>
        private void OnNewMatch() {
            _active.Clear();
            _applied = false;
            _roundSeen = 0;
            SR_UCH.Pure.RoundTruth.Reset(ref _rs);
            _roundAdvancePending = 0;
            _roster.Clear();
        }

        // ============ 抽牌与广播 ============

        private void DrawAndBroadcast() {
            // 只有权威端才能抽牌并广播。
            if (!IsAuthority) { SRApi.NotifyT("仅房主可抽牌", "Only the host can draw"); return; }
            if (_enabled == null || !_enabled.Value) return;
            if (!ModeAllowed()) return;                                 // 限制模式：静默跳过
            var chosen = new List<int>();
            for (int i = 0; i < Cards.Length; i++) if (_pool[i] != null && _pool[i].Value) chosen.Add(i);
            if (chosen.Count == 0) return;

            // 先按 0 复位上一回合生效的规则，否则未装 mod 的房客会逐回合累积。
            BroadcastResetPrevious();

            _drawSeq++;
            var rng = new System.Random(Environment.TickCount ^ (_roundSeen * 7919) ^ (_drawSeq * 104729));
            int want = Mathf.Clamp(_maxActive.Value, 1, 4);
            _active.Clear();
            // 洗牌后按顺序取：同一张牌不会重复抽到；同一规则只保留第一次出现的那张，
            // 否则规则覆盖会让实际生效数量少于设置值（例如要 4 张、牌池 2 张却只剩 2 条生效）。
            List<int> bag = new List<int>(chosen);
            for (int i = bag.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int tmp = bag[i]; bag[i] = bag[j]; bag[j] = tmp; }
            var usedRules = new List<TabletRule>();
            for (int i = 0; i < bag.Count && _active.Count < want; i++) {
                int id = bag[i];
                TabletRule rule = Cards[id].Rule;
                if (usedRules.Contains(rule)) continue;      // 同规则已被其它牌占用，跳过
                usedRules.Add(rule);
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
            // 区间取卡牌定义（无自定义）。
            int lo = c.Min, hi = c.Max;
            lo = Mathf.Max(1, lo);
            if (hi < lo) hi = lo;
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
                case FJump:     return m.JumpSpeedValues != null ? m.JumpSpeedValues.Length : 0;
                case FSprint:   return m.SprintModifiers != null ? m.SprintModifiers.Length : 0;
                case FPlatform: return m.PlatformMoveSpeedValues != null ? m.PlatformMoveSpeedValues.Length : 0;
            }
            return 0;
        }

        /// <summary>发一条真实规则消息；房客由游戏写 Modifiers，房主靠本地双写。</summary>
        private static void BroadcastOne(int cardId, int param, bool restore = false) {
            if (!NetClientActive()) return;
            CardDef c = Cards[cardId];
            try {
                int v2 = (CardMagic << 16) | ((restore ? OpCardRestore : OpCardDraw) << 8) | (cardId & 0xFF);
                if (c.IsBool) TabletModalOverlay.BroadcastRuleChange(c.Rule, 0, v2, param != 0);
                else TabletModalOverlay.BroadcastRuleChange(c.Rule, param, v2, false);
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/广播", ex); }
        }

        // ============ 本地落位（幂等：先清零再按序重建） ============

        private void RebuildLocal() {
            if (!IsAuthority) return;
            var m = SafeMods();
            if (m == null) return;
            ClearFields(m);
            for (int i = 0; i < _active.Count; i++) ApplyLocal(m, Cards[_active[i].Id], _active[i].Param);
            _applied = true;
        }

        // 全部修改器字段复位到游戏默认值；不猜"1 = 正常"，0/false 即序列化默认。
        private static void ClearFields(Modifiers m) {
            m.GravityMode = 0;
            m.JumpSpeedMode = 0;
            m.SprintSpeedMode = 0;
            m.PlatformSpeedMode = 0;
            m.RateOfFireMode = 0;
            m.wallJumpsDisabled = false;
            m.danceInvincibility = false;
            m.mirrorControls = false;
            m.playerPlayerCollisions = false;
            m.frictionless = false;
        }

        // 本机终态：字段归零 + 清牌面与状态；不广播。
        private void ClearLocal() {
            try { var m = SafeMods(); if (m != null) ClearFields(m); }
            catch (Exception __cl) { SRApi.Guard.Log("混乱牌组/本地清零", __cl); }
            _active.Clear();
            _applied = false;
            _roundSeen = 0;
            SR_UCH.Pure.RoundTruth.Reset(ref _rs);
            _roundAdvancePending = 0;
        }

        // 关闭开关：房主广播全场清零，随后本机落到同一终态。
        private void ClearAll(bool broadcast) {
            if (broadcast && IsAuthority) BroadcastClearAll();
            ClearLocal();
            SRApi.LogInfo("[混乱牌组] 已清空牌面与修改器内容（全场一致）");
        }

        // 抽牌前把上一回合生效的规则广播为 0；OpCardRestore 同时清掉房客的旧牌面。
        private static void BroadcastResetPrevious() {
            if (!NetClientActive() || _active.Count == 0) return;
            for (int i = 0; i < _active.Count; i++) BroadcastOne(_active[i].Id, 0, true);
        }

        // 真实规则消息给未装 mod 的房客（游戏原生写 0），带外信号让装 mod 的端全字段归零。
        private static void BroadcastClearAll() {
            if (!NetClientActive()) return;
            var seen = new List<TabletRule>();
            for (int i = 0; i < Cards.Length; i++) {
                bool dup = false;
                for (int k = 0; k < seen.Count; k++) if (seen[k] == Cards[i].Rule) { dup = true; break; }
                if (dup) continue;
                seen.Add(Cards[i].Rule);
                BroadcastOne(i, 0, true);
            }
            SendSignal(OpClearAll);
        }

        private static void ApplyLocal(Modifiers m, CardDef c, int param) {
            switch (c.Field) {
                case FGravity:  m.GravityMode = param; break;
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
            // 无中途加入，30 秒一刷只为换局 / 重连兜底。
            _nextHelloAt = Time.unscaledTime + 30f;
            // 名单带时间戳：旧条目 60 秒过期。
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

            
            if (m.NewRule == TabletRule.None && (m.Value2 >> 8) == Magic) {
                int op = m.Value2 & 0xFF;
                if (op == OpHello && m.Value >= 0 && m.Value < 8 && m.Value != LocalNetworkNumber())
                    _roster[m.Value] = new RosterEntry(Time.unscaledTime);
                else if (op == OpClearAll) { ClearLocal(); }
                return;
            }

            // 只认「房主发的、带牌组魔数」的真实规则消息；本机开关关掉也要只跟踪不生效，否则页面与真实状态不符。
            if (!IsOnline() || IsHostSelf()) return;
            if (!ModeAllowed()) return;                                 // 限制模式：不接收牌面
            if ((m.Value2 >> 16) != CardMagic) return;
            int op2 = (m.Value2 >> 8) & 0xFF;
            int card = m.Value2 & 0xFF;
            if (card < 0 || card >= Cards.Length) return;
            if (op2 == OpCardRestore) { _active.Clear(); _applied = false; return; }
            if (op2 != OpCardDraw) return;
            // 同规则多张牌只保留最新一张。
            for (int i = _active.Count - 1; i >= 0; i--)
                if (Cards[_active[i].Id].Rule == Cards[card].Rule) _active.RemoveAt(i);
            int param = Cards[card].IsBool ? (m.Valueb ? 1 : 0) : m.Value;
            _active.Add(new ActiveCard(card, param));
            _applied = true;
        }

        // ============ 对外展示 ============
        // 同一 Rule 只有最后写入的生效，显示按每条规则的最后一张收口，保证两端一致。
        private static int[] _effIdx = new int[4];
        private static int EffectiveIndexes() {
            if (_effIdx == null || _effIdx.Length < Mathf.Min(Cards.Length, 16)) _effIdx = new int[Mathf.Min(Cards.Length, 16)];
            int n = 0;
            for (int i = _active.Count - 1; i >= 0 && n < _effIdx.Length; i--) {
                int id = _active[i].Id;
                if (id < 0 || id >= Cards.Length) continue;
                TabletRule rule = Cards[id].Rule;
                bool dup = false;
                for (int k = 0; k < n; k++) if (Cards[_active[_effIdx[k]].Id].Rule == rule) { dup = true; break; }
                if (!dup) _effIdx[n++] = i;
            }
            
            for (int a = 0, b = n - 1; a < b; a++, b--) { int t = _effIdx[a]; _effIdx[a] = _effIdx[b]; _effIdx[b] = t; }
            return n;
        }

        public static string Desc() {
            // 用核心按帧缓存，同帧只算一遍。
            return SRApi.Cache.FrameSig("chaos.desc", delegate { return DescCore(); });
        }

        private static string DescCore() {
            if (!_ready) return null;
            int n = EffectiveIndexes();
            if (n == 0) return null;
            var parts = new List<string>();
            for (int k = 0; k < n; k++) {
                int i = _effIdx[k];
                var c = Cards[_active[i].Id];
                // 播报要发给房客，文案须跟随房主界面语言。
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
                parts.Add("#" + kv.Key);
            }
            string tail = parts.Count > 0
                ? string.Join(SRApi.Tid("6486d4e8b0", "、", ", "), parts.ToArray())
                : SRApi.Tid("4957c49e50", "尚未收到其他端回应", "no other clients replied yet");
            return SRApi.Tid("9e4c887019", "房内：本机已装 / 其他端 ", "Room: this machine installed / others ") + tail;
        }

    }
}
