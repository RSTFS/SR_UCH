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
    [SrFeature("sr.chaos", "Chaos", "2.2", Modes = (int)(SRApi.ModeMask.Party | SRApi.ModeMask.Creative))]
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

        private const int FGravity = 0, FJump = 1, FSprint = 2, FPlatform = 3, FRate = 4;
        private static int _drawSeq;   // 抽牌序号：保证每次重抽的随机种子都不同（否则 TickCount 同毫秒会抽出同一张）
        private const int FBoolWallJumps = 8, FBoolDance = 9, FBoolMirror = 10, FBoolPlayerCol = 12, FBoolFriction = 13;

        // 17 张牌：5 个数值规则的多档取值（重力 / 跳跃 / 冲刺 / 平台 / 射速）+ 5 个布尔规则。
        // 每张牌带一句人话效果（牌面卡片第二行）；牌名/效果都走 SRApi.T，跟随界面语言。
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
            new CardDef("慢射速",   "Slow fire rate",   "射箭速度变慢",             "Slower rate of fire",                  TabletRule.ModifierRateOfFire,         FRate,          false, 1, 3),
            new CardDef("高跳跃",   "High jump",        "跳跃高度提升",             "Higher jumps",                         TabletRule.ModifierJumpSpeed,          FJump,          false, 2, 3),
            new CardDef("快冲刺",   "Fast sprint",      "冲刺速度提升",             "Faster sprinting",                     TabletRule.ModifierSprintSpeed,        FSprint,        false, 2, 4),
            new CardDef("快平台",   "Fast platforms",   "平台移动变快",             "Platforms move faster",                TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 2, 4),
            new CardDef("超重",     "Crushing gravity", "重力极重，跳跃困难",       "Very heavy gravity, hard to jump",     TabletRule.ModifierGravity,            FGravity,       false, 3, 3),
            new CardDef("龟速冲刺", "Crawl sprint",     "冲刺慢到接近走路",         "Sprint barely faster than walking",    TabletRule.ModifierSprintSpeed,        FSprint,        false, 1, 1),
            new CardDef("停滞平台", "Frozen platforms", "平台几乎不动",             "Platforms barely move",                TabletRule.ModifierPlatformSpeed,      FPlatform,      false, 1, 1),
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
        private struct RosterEntry {
            public float At;
            public RosterEntry(float a) { At = a; }
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
            SRApi.Ctl.DrawEntryRow(e);
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

            // 混乱牌组「不存盘」（用户要求第一种）：启动时把本功能所有配置强制回到默认值，
            // 上一局写在 cfg 里的值一律不继承——效果上等于每次启动都是全新会话。
            // （cfg 文件仍可能被设置页写回，但下次启动会被这里覆盖，用户永远看不到"上次的设置还在"。）
            try {
                _enabled.Value = false;
                _everyRounds.Value = 1;
                _maxActive.Value = 1;
                _announce.Value = true;
                _drawKey.Value = KeyCode.None;
                for (int __i = 0; __i < _pool.Length; __i++) if (_pool[__i] != null) _pool[__i].Value = true;
            } catch (Exception __rs) { SRApi.Guard.Log("混乱牌组/会话重置", __rs); }

            _enabled.SettingChanged += (s2, e2) => { if (!_enabled.Value) RestoreAll(); };

            SRApi.NetMagic.Register("chaos.signal", Magic);
            SRApi.NetMagic.Register("chaos.card", CardMagic);
            SRApi.RegisterKey("混乱牌组-重抽", _drawKey, "press", delegate {
                if (!IsAuthority || !ModeAllowed()) return;
                if (_enabled != null && _enabled.Value) DrawAndBroadcast();
            });

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
            if (!ModeAllowed()) return;                                 // 限制模式：静默跳过
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
        // 混乱牌组只在对局允许的模式里生效：派对 / 创意（超级模式由 GateModeAllows 豁免）。
        // 其它模式一律静默跳过：不抽牌、不接收、不弹提示。
        private static bool ModeAllowed() {
            try {
                // 树屋里还没进入具体模式：允许手动重抽（否则「重抽」按钮看起来无效）。
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

        // ============ 阶段边界 ============

        private void OnStartPhase(GameEvent.StartPhaseEvent e) {
            if (!_ready || !_enabled.Value) return;
            if (e == null || e.Phase != GameControl.GamePhase.PLACE) return;

            // 房客端绝不本地重建：规则由房主广播 + 游戏写入 Modifiers。
            // 若在这里 RebuildLocal()，会用「已被房主改过的值」Capture 快照，
            // 再用空的 _active 还原，把房主的牌冲掉 —— 联机规则不同步的老坑。
            if (!IsAuthority) return;
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
            // P0（审查报告）：只有权威端（房主/离线）才能抽牌并广播；按钮是零成本触发路径，必须在源头拦截。
            if (!IsAuthority) { SRApi.NotifyT("仅房主可抽牌", "Only the host can draw"); return; }
            if (_enabled == null || !_enabled.Value) return;
            if (!ModeAllowed()) return;                                 // 限制模式：静默跳过
            var chosen = new List<int>();
            for (int i = 0; i < Cards.Length; i++) if (_pool[i] != null && _pool[i].Value) chosen.Add(i);
            if (chosen.Count == 0) return;

            // 关键：先把上一回合生效过的规则按树屋原值重发。
            // 房主本地 RebuildLocal 会「整体还原再重建」，但没装 mod 的房客只收到
            // 新牌那条规则消息 —— 不发还原，房客的规则会逐回合累积，与房主不一致。
            BroadcastRestoreRules();

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
            // UCH 锁房、没有中途加入，8 秒一刷纯属冗余流量；降到 30 秒只为换局/重连兜底。
            _nextHelloAt = Time.unscaledTime + 30f;
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
                if (op == OpHello && m.Value >= 0 && m.Value < 8 && m.Value != LocalNetworkNumber())
                    _roster[m.Value] = new RosterEntry(Time.unscaledTime);
                
                return;
            }

            // 只认「房主发的、带牌组魔数」的真实规则消息。
            // 玩家在规则书里手动改规则时 Value2 = 0，其它功能发的带外消息魔数也不同
            // —— 都不能被当成抽牌（否则会把牌面搞乱）。
            // 注：本机开关关掉也要继续「只跟踪不生效」——规则是房主广播的，游戏照样写进
            // Modifiers；如果这里按 _enabled 早退，页面会显示「未生效」而规则其实在生效（审计 P2-7）。
            if (!IsOnline() || IsHostSelf()) return;
            if (!ModeAllowed()) return;                                 // 限制模式：不接收牌面
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

        // 同一 Rule 只有最后写进去的那张真正生效（房主重建与房客端接收都是后写覆盖前写）。
        // 显示也按「每条规则的最后一张」收口：以前房主抽到两张同规则牌时会画两张，
        // 房客端只收到/只留一张，两端牌面不一致。
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
            // 翻转回抽牌顺序（从后往前收集是为了「后写者优先」）。
            for (int a = 0, b = n - 1; a < b; a++, b--) { int t = _effIdx[a]; _effIdx[a] = _effIdx[b]; _effIdx[b] = t; }
            return n;
        }

        public static string Desc() {
            // 同帧内 UI 与通知各调一次；用核心的按帧缓存只算一遍（帧号变了自动重算）。
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
                parts.Add("#" + kv.Key);
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

        // ============ 当前牌面：一张牌画成一张卡片（名称 + 效果 + 增益/减益色边） ============

        private static GUIStyle _cardTitle, _cardEffect;
        private static readonly Color CardBuff = new Color(0.42f, 0.88f, 0.54f, 1f);   // 增益：绿
        private static readonly Color CardDebuff = new Color(0.96f, 0.48f, 0.40f, 1f); // 减益：红橙
        private static readonly Color CardNeutral = new Color(0.56f, 0.72f, 1f, 1f);   // 中性/混合：蓝

        private static void EnsureCardStyles() {
            if (_cardTitle != null) return;
            _cardTitle = new GUIStyle(GUI.skin.label);
            _cardTitle.fontStyle = FontStyle.Bold;
            _cardTitle.wordWrap = false;
            _cardTitle.clipping = TextClipping.Clip;
            _cardTitle.alignment = TextAnchor.MiddleLeft;
            _cardTitle.padding = new RectOffset(0, 0, 0, 0);
            _cardTitle.margin = new RectOffset(0, 0, 0, 0);
            _cardTitle.normal.textColor = new Color(0.96f, 0.97f, 1f, 1f);
            _cardEffect = new GUIStyle(GUI.skin.label);
            _cardEffect.wordWrap = true;
            _cardEffect.clipping = TextClipping.Clip;
            _cardEffect.alignment = TextAnchor.UpperLeft;
            _cardEffect.padding = new RectOffset(0, 0, 0, 0);
            _cardEffect.margin = new RectOffset(0, 0, 0, 0);
            _cardEffect.normal.textColor = new Color(0.78f, 0.81f, 0.87f, 1f);
        }

        // 牌面边框色：让玩家变强 = 增益（绿），变弱 = 减益（红），两边都占/纯整活 = 中性（蓝）。
        private static Color CardTint(CardDef c) {
            if (c.Field == FBoolDance) return CardBuff;
            if (c.Field == FBoolWallJumps || c.Field == FBoolFriction || c.Field == FBoolMirror || c.Field == FBoolPlayerCol) return CardDebuff;
            if (c.Field == FGravity) return c.Min <= 1 ? CardBuff : CardDebuff;          // 低重力有益，高重力/超重有害
            if (c.Field == FJump) return c.Min >= 2 ? CardBuff : CardDebuff;
            if (c.Field == FSprint) return c.Min >= 2 ? CardBuff : CardDebuff;   // 快冲刺 Min=2
            if (c.Field == FPlatform) return c.Min >= 2 ? CardBuff : CardDebuff; // 快平台 Min=2
            if (c.Field == FRate) return CardDebuff;                                      // 慢射速 = 减益
            return CardNeutral;
        }

        private void RenderActiveCards() {
            int n = EffectiveIndexes();
            if (n <= 0) {
                if (_enabled != null && !_enabled.Value) Line("（本机未启用）", "(off on this machine)");
                else Line("（未生效）", "(none)");
                return;
            }
            EnsureCardStyles();
            float h = SRApi.Ctl.Sc(52f);
            float gap = SRApi.Ctl.Sc(6f);
            // 先占住整行高度：抽到 0/1/多张时页面不会跳，也不会因为卡片内容变化抖高度。
            Rect area = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
            float cw = Mathf.Max(SRApi.Ctl.Sc(72f), Mathf.Floor((area.width - gap * (n - 1)) / Mathf.Max(1, n)));
            for (int k = 0; k < n; k++) {
                int i = _effIdx[k];
                DrawCard(new Rect(area.x + k * (cw + gap), area.y, cw, h), Cards[_active[i].Id], _active[i].Param);
            }
            DrawCardLegend();
        }

        // 三色小图例：色边含义写在卡上不如画出来直观（详细说明仍在分区提示里）。
        private static void DrawCardLegend() {
            float h = SRApi.Ctl.Sc(14f);
            float chip = SRApi.Ctl.Sc(10f);
            Rect area = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
            _cardEffect.fontSize = Mathf.RoundToInt(SRApi.Ctl.Sc(10f));
            float x = area.x;
            float y = area.y + (h - chip) * 0.5f;
            CardLegendItem(ref x, y, chip, CardBuff, "增益", "buff");
            CardLegendItem(ref x, y, chip, CardDebuff, "减益", "debuff");
            CardLegendItem(ref x, y, chip, CardNeutral, "中性", "neutral");
        }

        private static void CardLegendItem(ref float x, float y, float chip, Color tint, string zh, string en) {
            SRApi.DrawFill(new Rect(x, y, chip, chip), tint);
            string text = SRApi.T(zh, en);
            float w;
            try { w = _cardEffect.CalcSize(new GUIContent(text)).x; } catch { w = SRApi.Ctl.Sc(36f); }
            GUI.Label(new Rect(x + chip + SRApi.Ctl.Sc(3f), y - SRApi.Ctl.Sc(2f), w + SRApi.Ctl.Sc(2f), chip + SRApi.Ctl.Sc(4f)), text, _cardEffect);
            x += chip + SRApi.Ctl.Sc(3f) + w + SRApi.Ctl.Sc(12f);
        }

        private static void DrawCard(Rect r, CardDef c, int param) {
            Color tint = CardTint(c);
            float bw = Mathf.Max(1f, SRApi.Ctl.Sc(2f));
            // 底色 + 四边色条（不用圆角面板：卡片每帧重画，纯矩形最省）
            SRApi.DrawFill(r, new Color(0.10f, 0.115f, 0.15f, 0.94f));
            SRApi.DrawFill(new Rect(r.x, r.y, r.width, bw), tint);
            SRApi.DrawFill(new Rect(r.x, r.yMax - bw, r.width, bw), tint);
            SRApi.DrawFill(new Rect(r.x, r.y, bw, r.height), tint);
            SRApi.DrawFill(new Rect(r.xMax - bw, r.y, bw, r.height), tint);

            float pad = SRApi.Ctl.Sc(6f);
            float titleH = SRApi.Ctl.Sc(17f);
            Rect inner = new Rect(r.x + pad, r.y + SRApi.Ctl.Sc(4f), r.width - pad * 2f, r.height - SRApi.Ctl.Sc(9f));
            _cardTitle.fontSize = Mathf.RoundToInt(SRApi.Ctl.Sc(13f));
            _cardEffect.fontSize = Mathf.RoundToInt(SRApi.Ctl.Sc(10f));
            GUI.Label(new Rect(inner.x, inner.y, inner.width, titleH), SRApi.T(c.Zh, c.En), _cardTitle);
            string effect = SRApi.T(c.TipZh, c.TipEn);
            if (!c.IsBool) effect += " · " + SRApi.T("值 ", "value ") + param;
            GUI.Label(new Rect(inner.x, inner.y + titleH, inner.width, inner.height - titleH), effect, _cardEffect);
        }

        private void RenderPage() {
            // 配置项一律走核心统一渲染；核心层没有裸 Toggle/Slider/Label 控件。
            // 限制模式（非派对/创意）下整页配置置灰：触发即跳过，不弹提示。
            using (SRApi.Ctl.Disabled(!ModeAllowed())) {
                CfgRow(_enabled);
                CfgRow(_everyRounds);
                CfgRow(_maxActive);
                CfgRow(_announce);
            }
            if (!ModeAllowed())
                Line("仅派对 / 创意模式可用（超级模式可无视模式限制）。", "Party / Creative only (super mode bypasses mode limits).");

            // 本机开关关掉时，牌面只作跟踪（房主规则仍然生效），必须明说，不能让页面假称"未生效"。
            if (_enabled != null && !_enabled.Value)
                Line("本机开关已关：这里只显示收到的牌面，房主规则仍会生效。",
                     "Switch is off on this machine: cards are display-only; host rules still apply.");
            SRApi.Ctl.SectionTitle("- " + SRApi.T("当前牌面", "Active cards") + " -",
                SRApi.T("本回合生效的规则牌：绿边 = 增益，红边 = 减益，蓝边 = 中性。",
                        "Rules in effect this round: green = buff, red = debuff, blue = neutral."));
            RenderActiveCards();
            using (SRApi.Ctl.Disabled(!IsAuthority || _enabled == null || !_enabled.Value)) {
                SRApi.Ctl.HotkeyButton("chaos.reroll", SRApi.T("重抽", "Draw"),
                                       SRApi.T("仅房主 / 离线有效", "Host or offline only"), DrawAndBroadcast);
            }
        }

        // 牌池单独一页：按「数值规则 / 布尔规则」分组勾选 + 批量操作（全选 / 全不选 / 仅增益 / 仅减益）。

        private void RenderPoolPage() {
            // 四个批量操作同排一行（紧凑按钮，避免占掉过多列表空间）。
            GUILayout.BeginHorizontal();
            float bh = SRApi.Ctl.Sc(24f), bw = SRApi.Ctl.Sc(64f);
            if (SRApi.Ctl.Button(SRApi.T("全选", "All"), SRApi.T("勾选全部牌", "Enable every card"), bw, bh)) SetAllPools(true);
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("全不选", "None"), SRApi.T("全部取消（会再自动勾回第一张）", "Disable all (the first card is re-enabled)"), bw, bh)) SetAllPools(false);
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("仅增益", "Buffs"), SRApi.T("只保留对自己有利的牌（高跳/快跑/跳舞无敌等）", "Keep only beneficial cards"), bw, bh)) KeepBeneficial();
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("仅减益", "Debuffs"), SRApi.T("只保留让人受限/变弱的牌（低重力/慢速/禁墙跳等）", "Keep only weakening cards"), bw, bh)) KeepHarmful();
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("反选", "Invert"), SRApi.T("勾选状态全部反转（至少留一张）", "Invert every checkbox (keep at least one)"), bw, bh)) InvertPools();
            GUILayout.EndHorizontal();
            int __on = 0;
            for (int i = 0; i < Cards.Length; i++) if (_pool[i] != null && _pool[i].Value) __on++;
            Line(SRApi.T("已启用 " + __on + " / " + Cards.Length + " 张；勾选后才会进入随机池。", __on + " / " + Cards.Length + " cards enabled; only checked cards can be drawn."),
                 SRApi.T("已启用 " + __on + " / " + Cards.Length + " 张；勾选后才会进入随机池。", __on + " / " + Cards.Length + " cards enabled; only checked cards can be drawn."));
            GUILayout.Space(SRApi.Ctl.Sc(6f));

            RenderPoolGroup(true);    // 数值规则
            RenderPoolGroup(false);   // 布尔规则
        }

        // 分组渲染：按数值/布尔规则分成两块（顺序固定，结构不随勾选变化）。
        // 配置条目显示名缓存：游戏名/语言变化时才重注册 LocKey（每帧重注册会让本地化缓存抖动）。
        private static string[] _poolLabelCache;

        private void RenderPoolGroup(bool numeric) {
            SRApi.Ctl.SectionTitle("- " + SRApi.T(numeric ? "数值规则" : "布尔规则", numeric ? "Numeric rules" : "Boolean rules") + " -");
            if (_poolLabelCache == null) _poolLabelCache = new string[Cards.Length];
            for (int i = 0; i < Cards.Length; i++) {
                if (Cards[i].IsBool == numeric || _pool[i] == null) continue;   // numeric=true 显示数值牌（IsBool=false）
                CardDef c = Cards[i];
                string ours = SRApi.T(c.Zh, c.En);
                string gm = GameRuleName(c.Rule);
                // 同一规则有多张牌（如低/高重力）时游戏名相同，追加本功能的档位名区分。
                string label = string.IsNullOrEmpty(gm) ? ours : (gm == ours ? gm : gm + " · " + ours);
                SRApi.LocDynamic(_pool[i], label, label);   // 行名跟随游戏语言（核心节流重注册）
                CfgRow(_pool[i]);   // 标准复选框行（不是按钮）
            }
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

        // 规则名优先用游戏自己的本地化文本（跟随游戏语言）；取不到再回退本功能的中英文名。
        private static readonly Dictionary<TabletRule, string> _ruleNameCache = new Dictionary<TabletRule, string>();
        private static string GameRuleName(TabletRule rule) {
            string cached;
            if (_ruleNameCache.TryGetValue(rule, out cached)) return cached;
            string found = null;
            try {
                string en = rule.ToString();
                // 游戏用 I2 Localization；术语可能是枚举名，也可能带前缀，逐个候选探测。
                string[] cands = new string[] { en, "Modifiers/" + en, "Modifier_" + en, "RULE_" + en, "Rule_" + en };
                for (int i = 0; i < cands.Length; i++) {
                    string t2 = I2.Loc.LocalizationManager.GetTranslation(cands[i], false, 0, true, true, null, null, false);
                    if (!string.IsNullOrEmpty(t2) && t2 != cands[i]) { found = t2; break; }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("混乱牌组/游戏规则名", __ex); }
            _ruleNameCache[rule] = found;
            return found;
        }

        // 牌池批量筛选用的倾向分类（与 Cards 顺序一一对应）：+1 增益 / 0 中性 / -1 减益。
        private static readonly sbyte[] CardMood = new sbyte[] {
            1, -1, -1, -1, -1, -1, -1,    // 低重力(有益) / 高重力 / 弱跳跃 / 慢冲刺 / 慢平台 / 禁墙跳 / 镜像
             1, -1, -1, -1,               // 跳舞无敌 / 玩家互撞 / 无摩擦 / 慢射速
             1,  1,  1,                   // 高跳跃 / 快冲刺 / 快平台
            -1, -1, -1                    // 超重 / 龟速冲刺 / 停滞平台
        };

        private void ApplyPoolMood(int sign) {
            try {
                for (int i = 0; i < Cards.Length; i++) {
                    if (_pool[i] == null) continue;
                    _pool[i].Value = i < CardMood.Length && CardMood[i] == sign;
                }
                bool any = false;
                for (int i = 0; i < _pool.Length; i++) if (_pool[i] != null && _pool[i].Value) { any = true; break; }
                if (!any && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;   // 至少留一张
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/按倾向筛选", ex); }
        }

        private void InvertPools() {
            try {
                bool any = false;
                for (int i = 0; i < _pool.Length; i++) {
                    if (_pool[i] == null) continue;
                    _pool[i].Value = !_pool[i].Value;
                    if (_pool[i].Value) any = true;
                }
                if (!any && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/反选", ex); }
        }

        private void KeepBeneficial() { ApplyPoolMood(1); }
        private void KeepHarmful() { ApplyPoolMood(-1); }
    }
}
