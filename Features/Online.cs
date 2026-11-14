// 联机栏目（侧栏顺序 90）：回家、房间列表（固定表头表格，可拖列宽 / 换序 / 隐藏列）；
// 表头与列拖拽交给核心 TableDraw，本页只提供列定义和点击回调。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    // 表格文案优先取游戏本地化（区域 / 模式 / 标签），与游戏内用词一致。
    [SrFeature("sr.online", "Online", "1.0")]
    public partial class Online : ITweak {


        
        private static ConfigEntry<string> _fState;   //筛选：房间状态（"" = 全部；join/full/playing）
        private static ConfigEntry<string> _fRegion, _fMode, _fTag;
        private static ConfigEntry<int> _searchTimeout;
        private static float SearchTimeout() {
            try { if (_searchTimeout != null) return (float)_searchTimeout.Value; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return 15f;
        }
        // 表格列宽（像素，逗号分隔）/ 表格高度（0 = 自动）
        private static ConfigEntry<string> _colWidths;
        private static ConfigEntry<int> _tableH;
        private static ConfigEntry<bool> _showQualityCoeff;    //游戏质量列是否附带技能匹配质量系数（Shift+表头点击切换）
        // 排序：-1 = 默认顺序。
        private static ConfigEntry<int> _sortColEntry;
        private static ConfigEntry<bool> _sortDescEntry;
        private static int _sortCol = -1;
        private static bool _sortDesc;

        // 运行时状态
        private static readonly List<Matchmaker.LobbyListInfo> _lobbies = new List<Matchmaker.LobbyListInfo>();
        private static int _lobbyCbCount;           // 本次搜索收到的回调数
        private static float _lobbyCbFirst = -1f;   // 首个回调时刻
        private static float _lobbyCbLast = -1f;    // 最近一次回调时刻
        private static SearchRefreshListener _refreshListener;

        // 游戏要求刷新房间搜索时跟着重查一次
        public class SearchRefreshListener : GameEvent.IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    GameEvent.SpecialUIEvent se = e as GameEvent.SpecialUIEvent;
                    if (se == null) return;
                    if (se.SpecialUIType != GameEvent.SpecialUIEvent.SpecialUI.REFRESHSEARCH) return;
                    SRApi.LogInfo("[联机] 游戏请求刷新搜索：跟随刷新房间列表");
                    RefreshLobbies();
                } catch (Exception __ex) { SRApi.Guard.Log("联机/刷新监听", __ex); }
            }
        }

        private static bool _lobbySearching;
        private static float _lobbySearchAt = -999f;
        private static string _lobbyMsg = "";
        // 错误提示需粘住，不覆盖统计文案。
        private static bool _lobbyErr;
        // 仅在列表或可见性变化时才重排。
        private static bool _lobbiesDirty;
        private static float _lobbySortAt = -999f;
        private static Vector2 _scrollRows, _scrollCols;
        private static string _openCombo = "";              //同时只允许一个下拉框展开
        private static float _abortDeadline, _forceDeadline, _forceRawDeadline;   //回家看门狗的三级时限
        private static float _returnAttemptedAt;   // 上次发起"返回主界面"的时刻（0 = 没发起过）
        private static HostEndedListener _hostEndedListener;   // 房主解散监听（幂等注册）
        private static float[] _cw;                         //当前列宽
        private static bool _widthsLoaded;                  // 列宽是否已初始化
        
        private static string _pendingJoin = "";   // 待加入队列：只在 ClearPendingJoin() 里清空，避免漏一处就重复加入

        // 待加入是否来自"切换加入"：强制回主界面时不能清。
        private static bool _pendingIsJoin;

        // 清空待加入队列的唯一入口。
        internal static void ClearPendingJoin(string reason) {
            if (string.IsNullOrEmpty(_pendingJoin)) { _pendingIsJoin = false; return; }
            _pendingJoin = "";
            _pendingIsJoin = false;
            SRApi.LogInfo("[联机] 已清空待加入队列（" + reason + "）");
        }

        private static bool _pendingUseCode;                 //true = 按房间码加入
        private static float _pendingGiveUpAt;               //挂起到这个时刻就放弃（30 秒）
        private static float _pendingWaitFrom;               // 本次待加入开始等待的时刻（用于超时加速与输入屏蔽上限）
        private static bool _pendingNudged;                  // 本次等待是否已放过一次加载遮罩
        private static string _joinCode = "";                //按钮行右边的房间码输入框（4 个大写字母）

        // LevelSelectController.FadeOut 是 private 序列化字段（场景内遮罩），FadeToLevel 等的就是它。
        private static readonly FieldInfo _fLsFadeOut =
            typeof(LevelSelectController).GetField("FadeOut", BindingFlags.Instance | BindingFlags.NonPublic);

        private const string SecOnline = "Online";
        private const string ColWidthsKey = "Column Widths 5";   // v5：删除技能系数列（加/删列即换键，v4 旧值由配置迁移裁剪）
        private const string TableHKey = "Table Height";
        private const string ColOrderKey = "Column Order";
        private const string HiddenColsKey = "Hidden Columns 5";   // v5：删除技能系数列（v4 旧值由配置迁移裁剪）
        // 默认隐藏列索引，取值须与 CombinedCol 一致。
        private const string DefaultHiddenCol = "9";
        // 旧列布局 12 列，索引 3 是已删除的「技能系数」列；Column Order 无版本号，加载时按老形状裁剪。
        private const int OldColCount = 12;
        private const int OldSkillCol = 3;

        // 默认列宽，顺序：区域 人数 质量 进度 模式 标签 房主 限制 标记 综合分 操作。
        private static readonly float[] DefWidths = { 62f, 53f, 76f, 66f, 80f, 50f, 86f, 78f, 50f, 65f, 70f };   // 人数 +20%（44→53）、综合分 +20%（54→65）

        public void Initialize(IFeatureHost plugin) {
            SRApi.LocSec(SecOnline, "联机", "Online");
            SRApi.Nav(SecOnline, 110);   // 侧栏顺序 90：会话(80) 之后、实验(100) 之前
            SRApi.RegisterPage(SecOnline, RenderOnlinePage);
            SRApi.RegisterPageWheel(OnLobbyWheel);   // 页面关滚动，房间列表自己吃滚轮
            
            SRApi.Ctl.RegisterFillScroll(SecOnline, delegate (float __w) { ScrollTableByWheelPublic(__w); });
            SRApi.RegisterTick("联机看门狗", TickOnline); // 每帧：回家看门狗 + 延时加入
            SRApi.RegisterTick("联机自动进入主菜单", TickAutoEnterMainMenu); // 加入成功后替玩家按一次确认
            // 换场景后复位加入瞬时状态。
            SRApi.RegisterSceneHook(delegate { ResetJoinStateOnScene(); });
            // 房主心跳超 10 秒时游戏会广播 REFRESHSEARCH；不跟着刷新则房间 id 全过期。
            try {
                _refreshListener = new SearchRefreshListener();
                GameEventManager.ChangeListener<GameEvent.SpecialUIEvent>(_refreshListener, true);
                SRApi.OnShutdown("联机/刷新监听", delegate {
                    try { if (_refreshListener != null) GameEventManager.ChangeListener<GameEvent.SpecialUIEvent>(_refreshListener, false); }
                    catch (Exception __ul) { SRApi.Guard.Log("联机/注销刷新监听", __ul); }
                });
            } catch (Exception __rl) { SRApi.Guard.Log("联机/刷新监听注册", __rl); }
            // 加入流程要用 EventSystem，禁用会卡在"正在进入"。
        SRApi.RegisterInputGateHold(() => !string.IsNullOrEmpty(_pendingJoin)
            && Time.unscaledTime - _pendingWaitFrom < 3f);
            // 预登记本页快捷键：本页自绘，不打开就渲染不到 HotkeyButton。
            SRApi.HotkeyAction("online.lobbies", "刷新列表", RefreshLobbies);
            SRApi.HotkeyAction("online.disband", "返回主界面", DisbandOrLeave);

            // 反射成员须登记，否则游戏改名后静默失效。
            try {
                SRApi.Ref.Override("LevelSelectController.FadeOut", "联机：强制返回主界面（放出加载遮罩）");
                // 保留反射：直接写 SteamMatchmaker 会引入 Steamworks 依赖。
                SRApi.Ref.Override("SteamMatchmaker.JoinSteamLobby", "联机：按 Steam 房间号加入");
                SRApi.Ref.Override("UnityMatchmaker.inPlatformLobbyNoRelay", "联机：判断是否在平台房间里");
            } catch (Exception __ro) { SRApi.Guard.Log("联机反射登记", __ro); }

            // 房主解散感知：订阅网络消息事件总线，长期存活故须幂等。
            try {
                if (_hostEndedListener == null) {
                    _hostEndedListener = new HostEndedListener();
                    GameEventManager.ChangeListener<NetworkMessageReceivedEvent>(_hostEndedListener, true);
                    // 关机时注销，否则重载会叠成多份回调。
                    SRApi.OnShutdown("联机/房主解散监听", delegate {
                        try {
                            if (_hostEndedListener != null) {
                                GameEventManager.ChangeListener<NetworkMessageReceivedEvent>(_hostEndedListener, false);
                            }
                        } catch (Exception __uh) { SRApi.Guard.Log("联机/注销房主解散监听", __uh); }
                        _hostEndedListener = null;
                    });
                }
            } catch (Exception __he) {
                _hostEndedListener = null;
                SRApi.Guard.Log("联机.房主解散监听", __he);
            }


            _fRegion = plugin.Config.Bind(SecOnline, "Filter Region", "", "按区域挑房间, 从下拉里选一个.");
            _fMode = plugin.Config.Bind(SecOnline, "Filter Mode", "", "按游戏模式挑房间, 从下拉里选一个.");
            _fTag = plugin.Config.Bind(SecOnline, "Filter Tag", "", "按房间标签挑房间, 从下拉里选一个.");
            _fState = plugin.Config.Bind(SecOnline, "Filter State", "", "按房间状态挑. 留空是全部, join 只看还能加入的, full 只看人已满的, playing 只看正在打的.");
            _searchTimeout = plugin.Config.Bind(SecOnline, "Search Timeout", 15,
                new ConfigDescription("搜房间最多等多少秒, 5 到 60. 到点还没收到搜索结束的信号就不等了. 网慢或者房间多的时候可以调大.",
                    new AcceptableValueRange<int>(5, 60)));
            _colWidths = plugin.Config.Bind(SecOnline, ColWidthsKey, "",
                "表格每一列多宽, 单位像素, 用逗号隔开. 在表头的分隔线上按住左键拖就能改. 按住 Shift 右键表格可以还原列宽和高度, 并把藏起来的房间放出来.");
            _tableH = plugin.Config.Bind(SecOnline, TableHKey, 0,
                new ConfigDescription("表格有多高, 单位像素, 填 0 让它跟着窗口走. 拖表格底边也能改.", new AcceptableValueRange<int>(0, 1000)));
            _colOrder = plugin.Config.Bind(SecOnline, ColOrderKey, "", "列的先后顺序, 填列号, 用逗号隔开. 留空就是默认顺序.");
            _hiddenColsEntry = plugin.Config.Bind(SecOnline, HiddenColsKey, DefaultHiddenCol,
                "藏起来的列, 填列号, 用逗号隔开. 右键表头可以藏一列, 按住 Shift 右键还原. 默认只藏了综合分那一列.");
            // 质量列是否附带技能匹配系数（Shift+点表头切换）。
            _showQualityCoeff = plugin.Config.Bind(SecOnline, "Show Quality Coeff", false,
                "游戏质量那一列要不要多显示一个技能匹配质量系数. 按住 Shift 点这一列的表头可以切换, 默认关.");
            _sortColEntry = plugin.Config.Bind(SecOnline, "Sort Column", -1,
                "房间列表按哪一列排序, 填列号, -1 表示默认排法: 能加入的排前面, 其余按综合分从高到低.");
            _sortDescEntry = plugin.Config.Bind(SecOnline, "Sort Descending", false, "排序是从大到小还是从小到大.");
            try {
                _sortCol = _sortColEntry.Value;
                _sortDesc = _sortDescEntry.Value;
                // 配置被手改成越界值时当作不排序。
                if (_sortCol < 0 || _sortCol >= ColCount || _sortCol == JoinCol) _sortCol = -1;
            } catch { _sortCol = -1; _sortDesc = false; }
            SRApi.LocKey(SecOnline, "Show Quality Coeff", "显示技能匹配质量", null);
            SRApi.LocDesc(SecOnline, "Show Quality Coeff", "匹配系数显示\n在质量列后附带 0-1 的技能匹配系数\n也可 Shift+点击该列列头切换", "Match coefficient\nappends a 0-1 skill-match coefficient to the quality column\nalso toggled by Shift+clicking that header");
        }

        // 清空房间列表与几何缓存，不动列宽 / 列序 / 隐藏列偏好。
        private static void ClearLobbyList() {
            try {
                _lobbies.Clear();
                _shownLobbies.Clear();
                _rowRectsPage.Clear(); _rowRows.Clear();
                _joinRectsPage.Clear(); _joinRows.Clear();
                _joinDownRow = -1; _joinDownId = null;
                _lobbiesDirty = true;
            } catch (Exception __ex) { SRApi.Guard.Log("清空房间列表", __ex); }
        }

        private static void Notes(string zh, string en) {
            SRApi.Notify(SRApi.T(zh, en));
        }

        // 软看门狗（房主解散专用）：只做"始终没回主界面就切场景"兜底，不动 LobbyManager。
        private static void ArmWatchdogSoft() {
            float t = Time.unscaledTime;
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            _forceRawDeadline = t + 8f;
        }

        private static void ArmWatchdog() {
            float t = Time.unscaledTime;
            // 三级时限必须全部重置，残留值会在加入别的房间后硬切场景。
            _abortDeadline = t + 1.5f;  //第 1 级：放出被等待的遮罩
            _forceDeadline = t + 4f;    //第 2 级：自己收尾 + 强制加载主界面
            _forceRawDeadline = 0f;     //第 3 级：本次不预置，由 ForceMainMenu / TickWatchdog 到点才设
        }

        // 解除看门狗：发出加入请求后必须调用；逃生条件 MainMenu 在加入成功后永不满足。
        private static void DisarmWatchdog() {
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            // 第三级同样要解除：它不看场景，到点即硬切。
            _forceRawDeadline = 0f;
        }
        // 回家统一走 Matchmaker.ReturnToMainMenu，勿自行 Disconnect 或走 BackToMainMenu。
        public static void GoMainMenu() {
            Notes("正在返回主界面", "Returning to the main menu");
            // 点返回主界面即取消待加入，否则退出后会立刻 DoJoin。
            ClearPendingJoin("界面重置");
            _pendingSteamId = 0;   // 取消待加入，Steam 房号一并作废
            // reason 传 null：自定义串会弹给玩家并覆盖游戏的 abortReason。
            try { SRApi.LogInfo("[联机] 请求返回主界面（reason 传 null，避免把调试串弹给玩家）"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            try { Matchmaker.ReturnToMainMenu(null); }
            catch (Exception e) { SRApi.LogWarn("返回主界面失败: " + e.Message); }
            // 先做无损部分（放遮罩、清缓存）；动状态机的收尾只交给看门狗。
            NudgeSplash();
            ClearLobbyList();
            try { SRApi.ForceResetUiState(); } catch (Exception __ex) { SRApi.Guard.Log("返回主界面：重置界面状态", __ex); }
            ArmWatchdog();
        }

        public static void DisbandOrLeave() {
            // 已在返回倒计时中：再点即强制立即返回。
            if (_abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f) { ForceMainMenu(); return; }
            // 看门狗已超时：同样按强制返回处理。
            try {
                // 只在"上次返回已卡 3 秒以上"时才强制，否则会打断收尾。
                if (_returnAttemptedAt > 0f && SceneManager.GetActiveScene().name != "MainMenu"
                    && Time.unscaledTime - _returnAttemptedAt > 3f) {
                    SRApi.LogWarn("[联机] 上次返回超过 3 秒仍未完成，本次点击按强制返回处理");
                    ForceMainMenu();
                    return;
                }
            } catch (Exception __b1) { SRApi.Guard.Log("联机/返回状态检查", __b1); }
            try {
                LobbyManager lm = LobbyManager.instance;
                bool host = false;
                // 房主判定失败会退回房客分支，故须留痕。
                try { host = lm != null && lm.IsHost; } catch (Exception __ex) { SRApi.Guard.Log("判定是否房主", __ex); }
                Notes(host ? "正在解散房间" : "正在退出房间", host ? "Disbanding the session" : "Leaving the session");
            } catch (Exception __ex) { SRApi.Guard.Log("退出联机提示", __ex); }
            // 房主先广播 HostEndedGame 让全员回主界面；房客只退自己。
            _returnAttemptedAt = Time.unscaledTime;
            GoMainMenu();
        }

        // 强制返回主界面，不等游戏加载流程；每步独立 try，顺序：遮罩 -> 断开销毁 -> 收界面 -> 加载 -> 硬切。
        public static void ForceMainMenu() {
            if (SceneManager.GetActiveScene().name == "MainMenu") {
                _abortDeadline = 0f; _forceDeadline = 0f; _forceRawDeadline = 0f;
                Notes("已在主界面", "Already in the main menu");
                return;
            }
            Notes("正在强制返回主界面", "Force-returning to the main menu");
            // 切换加入途中的待加入不能清，清掉等于丢掉目标房。
            if (_pendingIsJoin) SRApi.LogInfo("[联机] 强制返回主界面：保留切换加入的目标房（" + _pendingJoin + "）");
            else ClearPendingJoin("强制返回主界面");   // 其它情况取消待加入，否则退出后仍会被拉进目标房
            _pendingSteamId = 0; // Steam 房号随之作废
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            SRApi.LogWarn("[联机] 强制返回主界面（手动）");
            NudgeSplash();
            // 先走游戏的 ReturnToMainMenu；直接 Disconnect+Destroy 会与这条链并发导致重复销毁。
            bool __native = false;
            try {
                Matchmaker.ReturnToMainMenu("SR_UCH force");   // 静态方法
                __native = true;
            } catch (Exception __rt) { SRApi.Guard.Log("强制返回主界面：ReturnToMainMenu", __rt); }
            if (!__native) {
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        // 每步独立 try、失败继续，但异常必须记录。
                        try { lm.Disconnect("SR_UCH force"); } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：断开连接", __ex); }
                        try { UnityEngine.Object.Destroy(lm.gameObject); } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：销毁 LobbyManager", __ex); }
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：收尾联机状态", __ex); }
            }
            try { SRApi.ForceResetUiState(); } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：重置界面状态", __ex); }
            // 先记 deadline 再加载：LoadScene 异步不抛异常，顺序反了会让看门狗再切一次。
            _forceRawDeadline = Time.unscaledTime + 2f;   //5) 二级兜底
            try { SceneManagerWrapper.LoadScene("MainMenu"); }
            catch (Exception e) { SRApi.LogWarn("强制加载主界面失败: " + e.Message); }
        }

        private static void SetShow(UISplashScreen s) {
            try {
                if (ReferenceEquals(s, null)) return;             //已销毁的场景内遮罩也要改（协程读的是托管字段）
                if (s.State != UISplashScreen.STATE.SHOW) s.State = UISplashScreen.STATE.SHOW;
            } catch (Exception __ex) { SRApi.Guard.Log("放出加载遮罩", __ex); }
        }

        // 把被 FadeToLevel 等待的遮罩推到 SHOW。
        private static void NudgeSplash() {
            try { SetShow(LoadingInterstitialSplash.Instance); } catch (Exception __ex) { SRApi.Guard.Log("放出加载遮罩(Instance)", __ex); }
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null && _fLsFadeOut != null) {
                    SetShow(_fLsFadeOut.GetValue(lm.CurrentLevelSelectController) as UISplashScreen);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("放出加载遮罩(关卡选择)", __ex); }
        }


        // 取匹配器必须先用 IsInstantiated：Instance 惰性创建会有 Steam 侧副作用。
        private static Matchmaker MatchmakerOrNull() {
            try { return Matchmaker.IsInstantiated ? Matchmaker.Instance : null; }
            catch { return null; }
        }

        public static void RefreshLobbies() {
            if (!SRApi.GateMaster) return;   // 受 SR 总开关限制
            try {
                Matchmaker mm = MatchmakerOrNull();
                // 判空必须放最前：下面的分支会立刻调用 mm.GetType()。
                if (mm == null) {
                    _lobbyMsg = SRApi.T("联机未初始化：请先进入游戏的联机界面一次", "Online not initialised: open the game's online screen once first");
                    _lobbyErr = true;
                    return;
                }
                // 游戏自己搜索时不要抢回调：Matchmaker 只保存一个列表回调。
                bool gameSearching = false;
                try { gameSearching = Matchmaker.Instance.Searching; } catch (Exception __gs) { SRApi.Guard.Log("联机/查询游戏搜索状态", __gs); }
                if (gameSearching) {
                    _lobbyMsg = SRApi.T("游戏正在搜索房间，稍后再刷新", "The game is already searching; refresh again in a moment");
                    return;
                }
                _lobbies.Clear();
                ClearRegionCodeCache();
                _lobbySearching = true;
                _lobbyErr = false;          // 本次搜索开始，清掉上一次的错误提示
                _lobbyMsg = SRApi.T("搜索中...", "Searching...");
                _lobbySearchAt = Time.unscaledTime;
                _lobbyCbFirst = -1f; _lobbyCbLast = -1f; _lobbyCbCount = 0;
                mm.FindLobbies(new Matchmaker.LobbyListingCallback(OnLobbyInfo));
            } catch (Exception e) {
                _lobbySearching = false;
                _lobbyErr = true;
                _lobbyMsg = SRApi.T("搜索失败: ", "Search failed: ") + e.Message;
                SRApi.LogWarn("刷新房间列表失败: " + e.Message);
            }
        }

        private static void OnLobbyInfo(Matchmaker.LobbyListInfo info) {
            try {
                if (info == null) return;
                if (info.error) { _lobbySearching = false; _lobbyErr = true; _lobbyMsg = SRApi.T("搜索出错", "Search error"); return; }
                // 同一 sLobbyID 的回调可能重复：去重替换，否则会重复行与计数虚高。
                int exist = -1;
                for (int i = 0; i < _lobbies.Count; i++) {
                    if (_lobbies[i] != null && _lobbies[i].sLobbyID == info.sLobbyID) { exist = i; break; }
                }
                if (exist >= 0) _lobbies[exist] = info; else _lobbies.Add(info);
                float cbNow = Time.unscaledTime;
                if (_lobbyCbFirst < 0f) {
                    _lobbyCbFirst = cbNow;
                }
                _lobbyCbLast = cbNow;
                _lobbyCbCount++;
                if (_lobbyCbCount % 5 == 0) {
                }
                RegionCode(info);
                // 只标记"变了"，排序与统计延迟到渲染时做。
                _lobbiesDirty = true;
            } catch (Exception __ex) { SRApi.Guard.Log("房间列表回调", __ex); }
        }


        public static void TickOnline() {
            TickWatchdog();
            TickPendingJoin();
            TickJoinState();
        }

        // ---------- 加入成功后自动进入主菜单（替玩家完成那一次确认） ----------
        private static bool _autoEnterMainMenu;
        private static float _autoEnterDeadline = -1f;

        internal static void ArmAutoEnterMainMenu() {
            _autoEnterMainMenu = true;
            _autoEnterDeadline = Time.unscaledTime + 180f;
        }

        private static void TickAutoEnterMainMenu() {
            if (!_autoEnterMainMenu) return;
            try {
                if (Time.unscaledTime > _autoEnterDeadline) { _autoEnterMainMenu = false; return; }
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainMenu") return;
                // 走 MatchmakerOrNull()：Instance 惰性创建，没开过联机界面时访问会顺带 new 一个（Steam 侧副作用）。
            Matchmaker mm = MatchmakerOrNull();
            if (mm == null) return;
                if (mm == null || !mm.WaitingForController) return;
                bool hasPlayer = false;
                foreach (Player p in PlayerManager.GetInstance()) { if (p != null) { hasPlayer = true; break; } }
                if (hasPlayer) { _autoEnterMainMenu = false; return; }
                Controller ctrl = null;
                try {
                    ControllerMonitor cm = ControllerMonitor.Instance;
                    if (cm != null && cm.mainController != null) ctrl = cm.mainController.controller;
                } catch (Exception __cm) { SRApi.Guard.Log("联机/取主控制器", __cm); }
                // 没有主控制器时用游戏自己的键盘控制器。
                if (ctrl == null) {
                    try { ctrl = GameState.GetInstance().Keyboard; } catch (Exception __kb) { SRApi.Guard.Log("联机/取键盘控制器", __kb); }
                }
                if (ctrl == null) return;   // 连键盘控制器都没有就退回让玩家自己按
                MainMenuControl menu = UnityEngine.Object.FindObjectOfType<MainMenuControl>();
                if (menu == null) return;
                menu.JoinControllerToMainMenu(ctrl, false);
                _autoEnterMainMenu = false;
                SRApi.LogInfo("[联机] 已自动加入主菜单玩家，跳过手动按确认");
            } catch (Exception __ex) { SRApi.Guard.Log("联机/自动进入主菜单", __ex); }
        }


        // 游戏本地化小工具：取不到则退回默认值。
        private static int _renderFrame = -1000;   // 本页最近一次被渲染的帧（滚轮钩子只在自己页面上工作）
        private static void RenderOnlinePage() {
            SRApi.NoPageScrollBars = true;   // 填充模式：列表占满剩余区域，页面本身不滚动
            _renderFrame = Time.frameCount;
            // 本页无页面级滚动（表头 / 筛选常驻）；不要每帧清零页面滚动。
            EnsureWidths();
            
            GUILayout.BeginHorizontal();
            if (SRApi.Ctl.HotkeyButton("online.lobbies", SRApi.T("刷新列表", "Refresh"),
                SRApi.T("向匹配服务查一次公开房间, 能加入的排在表格最上面, 点里面的加入就行.\n如果你正待在某个房间里, 点加入会先退出当前房间, 退完立刻自动进新的.",
                     "Query the public lobby list, joinable lobbies on top. Click Join in the table.\nIf you are already in a lobby, clicking Join leaves it first and joins the new one right away."),
                RefreshLobbies, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(30))) RefreshLobbies();
            // 返回倒计时中按钮改为"强制返回"。
            bool returning = _abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f;
            if (SRApi.Ctl.HotkeyButton("online.disband",
                returning ? SRApi.T("强制返回", "Force return") : SRApi.T("返回主界面", "Back to main menu"),
                SRApi.T("回主界面, 走的就是游戏自己的离开房间流程. 房主会先广播 HostEndedGame, 全员一起回去; 房客只退自己.\n点下去当帧就放出加载遮罩并清掉本页房间列表; 1.5 秒还没回去就自己收尾, 4 秒硬切主界面.\n再点一次这个按钮 = 立刻强制返回, 完全不等游戏自己的加载流程.",
                     "Back to the main menu, using the game's own leave-lobby flow. Host: broadcasts HostEndedGame and everyone returns; guest: only you leave.\nThe moment you click it releases the loading splash and clears this page's lobby list; self-recovery at 1.5s / 4s.\nClick it again to force an immediate return, skipping the game's own loading flow."),
                DisbandOrLeave, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(30))) DisbandOrLeave();
            // 房间码加入，走游戏的 JoinLobby(code, useCode:true)。
            GUILayout.Space(SRApi.Ctl.Sc(14));
            GUILayout.Label(new GUIContent(SRApi.T("房间码", "Lobby code"),
                SRApi.T("输入 4 个大写字母的房间码直接加入（和游戏里按码加入一样；在对局内会先退出当前房间）。",
                     "Enter the 4-letter lobby code to join directly (same as the game's join-by-code; while in a session it leaves the current one first).")),
                SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(CodeLabelW)), GUILayout.Height(SRApi.Ctl.Sc(30)));
            string typed = SRApi.ClearableTextField(_joinCode ?? "", 4, SRApi.Ctl.SearchBox,
                GUILayout.Width(SRApi.Ctl.Sc(62)), GUILayout.Height(SRApi.Ctl.Sc(24)));
            _joinCode = CodeOnly(typed);   // 只收大写英文字母，最多 4 个
            bool enter = Event.current != null && Event.current.type == EventType.KeyDown
                         && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                         && _joinCode.Length == 4;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = _joinCode.Length == 4;   // 不足 4 位不可点
            bool clickJoin = GUILayout.Button(SRApi.T("加入", "Join"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(46)), GUILayout.Height(SRApi.Ctl.Sc(24)));
            GUI.enabled = oldEnabled;
            if (_joinCode.Length == 4 && (clickJoin || enter)) JoinByCode(_joinCode);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 搜索结束的权威信号是 Searching（须先 IsInstantiated），超时仅兜底。
            if (_lobbySearching) {
                bool gameDone = false;
                try { if (Matchmaker.IsInstantiated) gameDone = !Matchmaker.Instance.Searching; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (gameDone || Time.unscaledTime - _lobbySearchAt > SearchTimeout()) {
                    _lobbySearching = false;

                        }
            }
            // 列表或可见性变化时重排，否则最多每 1 秒一次。
            if (_lobbiesDirty || Time.unscaledTime - _lobbySortAt >= 1f) {
                _lobbySortAt = Time.unscaledTime;
            }
            // 统计文案每帧重算；错误提示粘住。
            if (!_lobbyErr) UpdateLobbyMsg();
            string hint = SRApi.T("（点刷新列表查询）表头：左键点=按该列排序（升->降->默认），拖分隔线=调列宽（<->），拖表头=换列位置（+），Shift+点游戏质量=显示/隐藏匹配质量系数，右键=隐藏该列，Shift+右键=还原；滚轮=上下，Ctrl+滚轮=左右；表格底边可拖高度",
                               "(click 'Refresh' to query) Header: left-click = sort by that column (asc/desc/default), drag a separator = resize (<->), drag a header = reorder (+), Shift+click 'Quality' = show/hide the skill-match coefficient, right-click = hide that column, Shift+right-click = reset; wheel = up/down, Ctrl+wheel = left/right; drag the table's bottom edge to resize height");
            GUILayout.Label(string.IsNullOrEmpty(_lobbyMsg) ? hint : _lobbyMsg, SRApi.Ctl.Label);

            // 筛选单行：区域 / 模式 / 标签 / 状态，下拉框宽度按 4 等分。
            float cellW = Mathf.Max(SRApi.Ctl.Sc(96), (SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(40)) / 4f - SRApi.Ctl.Sc(8));
            GUILayout.Space(SRApi.Ctl.Sc(2));
            GUILayout.BeginHorizontal();
            DropFilter("区域", "Region", "按服务器区域筛选（亚太/欧盟/美国）。", "Filter by server region (AP / EU / US).", "Filter Region", RegionVals(), cellW);
            DropFilter("模式", "Mode", "按游戏模式筛选。", "Filter by game mode.", "Filter Mode", ModeVals, cellW);
            DropFilter("标签", "Tag", "按房间标签筛选。", "Filter by lobby tag.", "Filter Tag", TagVals, cellW);
            DropFilter("状态", "State", "按房间状态筛选：可加入 / 人满 / 对局中。", "Filter by lobby state: joinable / full / in match.", "Filter State", StateVals, cellW);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            
            GUILayout.Space(SRApi.Ctl.Sc(3));
            // 宽度用纯尺寸计算：两种坐标系混算会让表格框超宽、操作列被推出屏幕。
            float availW = Mathf.Max(SRApi.Ctl.Sc(220), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(12));
            // 高度只算一次，唯一真值 = 页面内容底界；多算法夹取会让 maxY 与可视高度脱节。
            Rect probe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
            float __minH = SRApi.Ctl.TableHeaderH() + SRApi.Ctl.TableRowH();
            float boxH = Mathf.Max(__minH, SRApi.PageContentBottom - probe.y);
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Width(availW), GUILayout.Height(boxH));

            TableDraw(box);
            TableInput(box, Event.current);

        }



        // 标签与下拉框都用固定宽度，否则两行不齐。
        private const float FilterLabelW = 46f;
        private const float FilterComboW = 104f;
        
        private static readonly string[] StateVals = { "join", "full", "playing" };   //#4 状态筛选

        // 下拉选项按版本戳缓存，变化时才重建。
        private static readonly Dictionary<string, string[]> _optCache = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, string> _optStamp = new Dictionary<string, string>();
        // 区域选项固定取自 RelayConstants.AVAILABLE_REGIONS，经 RegionCodeOf 归并成三大区，认不出保留原名。
        private static string[] _regionValsCache;
        private static int _regionValsVer = -1;







        // 与 FilterOptions 一一对应的值数组。
        private static readonly Dictionary<string, string[]> _optValsCache = new Dictionary<string, string[]>();


        // 筛选标签宽度按最长的标签实测。
        private static float _filterLabelW = -1f;
        private static string _filterLabelLKey;










        // 列宽 / 列序 / 隐藏列由核心 TableState 接管。
        private static void InitCols() {
            if (_cols != null) return;
            try {
                float[] dw = new float[ColCount];
                for (int i = 0; i < ColCount && i < DefWidths.Length; i++) dw[i] = SRApi.Ctl.Sc(DefWidths[i]);
                _cols = new SRApi.TableState(_colWidths, _colOrder, _hiddenColsEntry, dw);
                _cw = _cols.Widths;
                _order = _cols.Order;
                _hiddenCols.Clear();
                foreach (int __h in _cols.Hidden) _hiddenCols.Add(__h);
                _widthsLoaded = true;
            } catch (Exception __ex) { SRApi.Guard.Log("联机/列状态初始化", __ex); }
        }

        private static void EnsureWidths() {
            InitCols();
            if (_cols != null) { _widthsLoaded = true; return; }
            if (_widthsLoaded) return;
            _widthsLoaded = true;   // 列宽填上后不再重进
            _cw = (float[])DefWidths.Clone();
            _order = new int[ColCount];
            for (int i = 0; i < ColCount; i++) _order[i] = i;
            try {
                string v = _colWidths != null ? _colWidths.Value : null;
                if (!string.IsNullOrEmpty(v)) {
                    string[] p = v.Split(',');
                    // 兼容旧版 12 项值：先裁掉已删除的列再套用，否则整串被丢弃。
                    if (p.Length == OldColCount) {
                        List<string> q = new List<string>(ColCount);
                        for (int i = 0; i < p.Length; i++) { if (i != OldSkillCol) q.Add(p[i]); }
                        p = q.ToArray();
                    }
                    if (p.Length == _cw.Length) {
                        for (int i = 0; i < p.Length; i++) {
                            float f;
                            if (float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f >= 28f && f <= 600f) {
                        _cw[i] = f;
                        // 人数 / 综合分旧值更窄时抬高到新默认，避免表头被截。
                        if ((i == 1 || i == 9) && _cw[i] < DefWidths[i]) _cw[i] = DefWidths[i];
                    }
                        }
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("读取联机表格列宽", __ex); } // 一次性：读不到就用默认列宽，留痕
            EnsureColOrder();
            EnsureHiddenCols();
        }

        // 列顺序须是 0..ColCount-1 的排列；老值 12 项含已删除的列 3，先去掉并把其后索引 -1，仍不合法就回默认。
        private static void EnsureColOrder() {
            try {
                string v = _colOrder != null ? _colOrder.Value : null;
                if (string.IsNullOrEmpty(v)) return;
                string[] p = v.Split(',');
                int[] src = new int[p.Length];
                for (int i = 0; i < p.Length; i++) {
                    if (!int.TryParse(p[i], out src[i])) return;
                }
                if (src.Length == OldColCount) {
                    List<int> mapped = new List<int>(ColCount);
                    for (int i = 0; i < src.Length; i++) {
                        int k = src[i];
                        if (k == OldSkillCol) continue;   // 已删除的技能系数列
                        if (k > OldSkillCol) k -= 1;
                        mapped.Add(k);
                    }
                    src = mapped.ToArray();
                }
                if (src.Length != ColCount) return;
                bool[] seen = new bool[ColCount];
                int[] o = new int[ColCount];
                for (int i = 0; i < ColCount; i++) {
                    int k = src[i];
                    if (k < 0 || k >= ColCount || seen[k]) return;
                    seen[k] = true;
                    o[i] = k;
                }
                _order = o;
            } catch (Exception __ex) { SRApi.Guard.Log("读取联机列顺序", __ex); }
        }

        // 仍逐项越界裁剪，防手改把不存在的列加进隐藏集合。
        private static void EnsureHiddenCols() {
            try {
                string v = _hiddenColsEntry != null ? _hiddenColsEntry.Value : null;
                if (string.IsNullOrEmpty(v)) return;
                foreach (string s in v.Split(',')) {
                    int k;
                    if (int.TryParse(s, out k) && k >= 0 && k < ColCount && k != JoinCol) _hiddenCols.Add(k);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("读取联机隐藏列", __ex); }
        }

        private static void SaveCols() {
            try {
                InitCols();
                if (_cols == null) return;
                _cols.Order = _order;
                _cols.Hidden.Clear();
                foreach (int __h in _hiddenCols) _cols.Hidden.Add(__h);
                _cols.Save();
            } catch (Exception __ex) { SRApi.Guard.Log("保存列设置", __ex); }
        }

        private static void SaveWidths() { SaveCols(); }

        
        private const int ColCount = 11;         // v5：删除技能系数列后的列数
        private const int CombinedCol = 9;       // 综合分列 = LobbyHealthNum + LobbySkillNum（游戏排序用的房间质量分）
        private const int JoinCol = 10;          //操作列：不可隐藏（否则没法加入）
        private static readonly string[] Titles = { "区域", "人数", "游戏质量", "进度", "游戏模式", "标签", "房主", "限制", "标识", "综合分", "操作" };
        private static readonly string[] TitlesEn = { "Region", "Players", "Quality", "Progress", "Mode", "Tag", "Host", "Limit", "Flags", "Combined", "Operation" };
        private static readonly string[] TitleTips = {
            "服务器区域",
            "当前人数（人满不可加入）",
            "✓ = 房主质量分达到 OkMatchScore，✓✓ = 达到 GoodMatchScore，再一个 ✓ = 匹配质量系数达到 SkillMatchQualityThreshold。\n系数 = CalculatedSkillMatchQuality（0~1）：游戏用大厅玩家技能串算出的技能匹配质量，越接近 1 = 房里玩家水平和本地玩家越接近（匹配越好）。\nShift+单击本列表头=显示/隐藏这个系数（默认不显示，只显示 ✓；开启后本列在 ✓ 后追加 0~1 的系数）。",
            "游戏自己的对局进度：大厅中  / 剩余百分比",
            "游戏模式",
            "房间标签",
            "房主名字",
            "制胜积分 / 长度限制",
            "标识",
            "房间综合分 = 房主质量分 + 技能换算分（游戏自己排序用的房间质量分，越大越靠前）。",
            "点加入进房（在对局内会先退出当前房间，回到界面就自动加入）",
        };
        private static readonly string[] TitleTipsEn = {
            "Server region",
            "Players(a full lobby cannot be joined)",
            "✓ = host quality reaches OkMatchScore, ✓✓ = reaches GoodMatchScore, plus another ✓ when the skill-match coefficient reaches SkillMatchQualityThreshold.\nCoefficient = CalculatedSkillMatchQuality (0-1): the game's skill-match quality computed from the lobby's player skills; closer to 1 = the players' skill is closer to yours.\nShift+click this header to show/hide that coefficient (hidden by default, marks only; when ON the column appends the 0-1 coefficient after the marks).",
            "The game's own match progress: in lobby / remaining percent",
            "Game mode",
            "Lobby tag",
            "Host name",
            "Win points / length limit",
            "Flags",
            "Combined score = host quality score + skill-derived score (the game's own ordering key; higher ranks first). Hidden by default: right-click this header to keep it visible.",
            "Click Join to enter. While in a session it leaves the current one first and joins automatically.",
        };

        private static SRApi.TableState _cols;   // 列宽/列序/隐藏列（核心统一持久化）
        private static int[] _order;
        private static readonly HashSet<int> _hiddenCols = new HashSet<int>();
        private static ConfigEntry<string> _colOrder, _hiddenColsEntry;
        private static Rect _rowsClipPage, _joinHeaderPage;

        
        private static void HeaderClickOnce(int ci) {
            if (_clickFrame == Time.frameCount) return;
            _clickFrame = Time.frameCount;
            HeaderClick(ci);
        }

    }
}
