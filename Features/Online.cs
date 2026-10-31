// 联机栏目：回家、房间列表（筛选 + 可拖列宽 / 换序 / 隐藏列的固定表头表格）。
// 表头绘制与列拖拽换序交给核心 TableDraw，本页只提供列定义和点击回调。
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
    // 联机栏目（侧栏顺序 90）：回家、房间列表、房间码加入。
    // 表格文案优先取游戏自己的本地化（区域 / 模式 / 标签 / I2 术语），与游戏内列表用词一致。
    [SrFeature("sr.online", "Online", "1.0")]
    public partial class Online : ITweak {


        // 筛选与表格配置（下拉框存字符串，空 = 全部 / 默认）
        private static ConfigEntry<string> _fState;   //筛选：房间状态（"" = 全部；join/full/playing）
        private static ConfigEntry<string> _fRegion, _fMode, _fTag;
        private static ConfigEntry<int> _searchTimeout;
        private static float SearchTimeout() {
            try { if (_searchTimeout != null) return (float)_searchTimeout.Value; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return 15f;
        }
        // 表格列宽（像素，逗号分隔；表头分隔线可拖动）/ 表格高度（0 = 自动）
        private static ConfigEntry<string> _colWidths;
        private static ConfigEntry<int> _tableH;
        private static ConfigEntry<bool> _showQualityCoeff;    //游戏质量列是否附带技能匹配质量系数（Shift+表头点击切换）
        // 多列排序：-1 = 不按列排序，回到默认顺序（可加入优先 -> 健康分降序 -> 进度 -> id）。
        private static ConfigEntry<int> _sortColEntry;
        private static ConfigEntry<bool> _sortDescEntry;
        private static int _sortCol = -1;
        private static bool _sortDesc;

        // 运行时状态
        private static readonly List<Matchmaker.LobbyListInfo> _lobbies = new List<Matchmaker.LobbyListInfo>();
        private static int _lobbyCbCount;           // 本次搜索收到的回调数
        private static float _lobbyCbFirst = -1f;   // 首个回调时刻
        private static float _lobbyCbLast = -1f;    // 最近一次回调时刻
        private static bool _lobbySearching;
        private static float _lobbySearchAt = -999f;
        private static string _lobbyMsg = "";
        // 错误提示需保持显示：_lobbyErr 时统计文案不得覆盖 _lobbyMsg。
        private static bool _lobbyErr;
        // 仅当房间列表或可见性变化时才重排与重算统计。
        private static bool _lobbiesDirty;
        private static float _lobbySortAt = -999f;
        private static Vector2 _scrollRows, _scrollCols;
        private static string _openCombo = "";              //同时只允许一个下拉框展开
        private static float _abortDeadline, _forceDeadline, _forceRawDeadline;   //回家看门狗的三级时限
        private static float _returnAttemptedAt;   // 上次发起"返回主界面"的时刻（0 = 没发起过）
        private static HostEndedListener _hostEndedListener;   // 房主解散监听（幂等注册）
        private static float[] _cw;                         //当前列宽
        private static bool _widthsLoaded;                  // 列宽是否已初始化
        // 对局内点加入：先退出当前房间，退出后立刻加入。
        private static string _pendingJoin = "";   // 待加入队列：只在 ClearPendingJoin() 里清空，避免漏一处就重复加入

        // 清空待加入队列的唯一入口（评审 B-P0-10）。reason 只为日志留痕，不影响逻辑。
        internal static void ClearPendingJoin(string reason) {
            if (string.IsNullOrEmpty(_pendingJoin)) return;
            _pendingJoin = "";
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
        // 默认隐藏列的列索引字符串，取值必须与 CombinedCol 一致。
        private const string DefaultHiddenCol = "9";
        // v5 之前的列布局：12 列，索引 3 是已删除的「技能系数」列。
        // Column Order 没有版本号（沿用旧键），所以加载时按老形状裁剪；Column Widths / Hidden Columns 已换键，
        // 由 SR.ConfigMigration 把 v4 的旧值裁剪后写进 v5 键（否则旧索引会指到别的列 = 藏错列）。
        private const int OldColCount = 12;
        private const int OldSkillCol = 3;

        // 默认列宽，顺序：区域 人数 游戏质量 进度 游戏模式 标签 房主 限制 标记 综合分 操作。
        private static readonly float[] DefWidths = { 62f, 53f, 76f, 66f, 80f, 50f, 86f, 78f, 50f, 65f, 70f };   // 人数 +20%（44→53）、综合分 +20%（54→65）

        public void Initialize(IFeatureHost plugin) {
            SRApi.LocSec(SecOnline, "联机", "Online");
            SRApi.Nav(SecOnline, 90);   // 侧栏顺序 90：会话(80) 之后、实验(100) 之前
            SRApi.RegisterPage(SecOnline, RenderOnlinePage);
            SRApi.RegisterTick("联机看门狗", TickOnline); // 每帧：回家看门狗 + 延时加入
            SRApi.RegisterTick("联机自动进入主菜单", TickAutoEnterMainMenu); // 加入成功后替玩家按一次确认
            // 加入流程本身要用 EventSystem，禁用会卡在"正在进入"：登记门控挂起，让核心不禁用 EventSystem。
            // 屏蔽输入上限 3 秒，超过即不再屏蔽（pending 最长可挂 30 秒）。
        SRApi.RegisterInputGateHold(() => !string.IsNullOrEmpty(_pendingJoin)
            && Time.unscaledTime - _pendingWaitFrom < 3f);
            // 预登记本页自动快捷键：本页自绘，不打开就渲染不到 HotkeyButton，而这两个键需在房间内随时可用。
            SRApi.HotkeyAction("online.lobbies", "刷新列表", RefreshLobbies);
            SRApi.HotkeyAction("online.disband", "返回主界面", DisbandOrLeave);

            // 反射成员登记：本文件的反射同样要登记，否则游戏改名后静默失效。
            try {
                SRApi.Ref.Override("LevelSelectController.FadeOut", "联机：强制返回主界面（放出加载遮罩）");
                // JoinSteamLobby 保留反射：源码里写 SteamMatchmaker 会引入 Steamworks 程序集依赖。
                SRApi.Ref.Override("SteamMatchmaker.JoinSteamLobby", "联机：按 Steam 房间号加入");
                SRApi.Ref.Override("UnityMatchmaker.inPlatformLobbyNoRelay", "联机：判断是否在平台房间里");
            } catch (Exception __ro) { SRApi.Guard.Log("联机反射登记", __ro); }

            // 房主解散感知：订阅游戏的网络消息事件总线；房主返回主界面会 SendToAll(HostEndedGame)。
            // 幂等：这个订阅是长期存活的全局监听，重复 Initialize（热重载 / 场景重建）会叠成 N 份回调。
            try {
                if (_hostEndedListener == null) {
                    _hostEndedListener = new HostEndedListener();
                    GameEventManager.ChangeListener<NetworkMessageReceivedEvent>(_hostEndedListener, true);
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
            // 表头切换（见 HeaderClick）：游戏质量列 = 是否附带技能匹配质量系数（Shift+点该列表头）。
            _showQualityCoeff = plugin.Config.Bind(SecOnline, "Show Quality Coeff", false,
                "游戏质量那一列要不要多显示一个技能匹配质量系数. 按住 Shift 点这一列的表头可以切换, 默认关.");
            // 多列排序：配置只保留读取，不再有表头点击写法（见 HeaderClick）。
            _sortColEntry = plugin.Config.Bind(SecOnline, "Sort Column", -1,
                "房间列表按哪一列排序, 填列号, -1 表示默认排法: 能加入的排前面, 其余按综合分从高到低.");
            _sortDescEntry = plugin.Config.Bind(SecOnline, "Sort Descending", false, "排序是从大到小还是从小到大.");
            try {
                _sortCol = _sortColEntry.Value;
                _sortDesc = _sortDescEntry.Value;
                // 配置被手改成越界值或指向操作列时当作不排序。
                if (_sortCol < 0 || _sortCol >= ColCount || _sortCol == JoinCol) _sortCol = -1;
            } catch { _sortCol = -1; _sortDesc = false; }
            SRApi.LocKey(SecOnline, "Show Quality Coeff", "显示技能匹配质量", null);
            SRApi.LocDesc(SecOnline, "Show Quality Coeff", "匹配系数显示\n在质量列后附带 0-1 的技能匹配系数\n也可 Shift+点击该列列头切换", "Match coefficient\nappends a 0-1 skill-match coefficient to the quality column\nalso toggled by Shift+clicking that header");
        }

        // 清空房间列表与表格几何缓存（返回主界面 / 房主解散时调用）；不动列宽 / 列序 / 隐藏列偏好。
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

        // 回家（解散 / 退出 / 返回主界面）
        private static void Notes(string zh, string en) {
            SRApi.Notify(SRApi.T(zh, en));
        }

        // 软看门狗（房主解散专用）：只做"最后还是没回主界面就切场景"这一级兜底，
        // 不动 LobbyManager、不发 Disconnect。房主解散时游戏自己会走正常收尾，
        // 这时再让硬看门狗去销毁 LobbyManager 会把游戏的清理流程打断（audit P0-20/21）。
        private static void ArmWatchdogSoft() {
            float t = Time.unscaledTime;
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            _forceRawDeadline = t + 8f;
        }

        private static void ArmWatchdog() {
            float t = Time.unscaledTime;
            // 三级时限必须全部重置：残留的 _forceRawDeadline 会在改主意加入别的房间后仍把场景硬切走。
            // 第 1 级只放遮罩（无损），第 2 级开始动游戏状态机。
            _abortDeadline = t + 1.5f;  //第 1 级：放出被等待的遮罩
            _forceDeadline = t + 4f;    //第 2 级：自己收尾 + 强制加载主界面
            _forceRawDeadline = 0f;     //第 3 级：本次不预置，由 ForceMainMenu / TickWatchdog 到点才设
        }

        // 解除看门狗：换房间路径在真正发出加入请求后必须调用一次。
        // 看门狗的逃生条件是场景变成 MainMenu，加入成功后的场景是 Lobby/TreeHouseLobby，永不满足。
        private static void DisarmWatchdog() {
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            // 第三级同样必须解除：它不看场景是否已换成功，到点即硬切主界面。
            _forceRawDeadline = 0f;
        }
        // 回家：统一走游戏自己的 Matchmaker.ReturnToMainMenu(reason)，收尾全部交给 LobbyManagerManager
        // （SetAbortReason -> 房主 SendToAll(MsgHostEndedGame) + CleanUpPlayers -> 断开并销毁 LobbyManager -> 加载 MainMenu）。
        // 不要自己先 Disconnect，也不要走 LevelSelectController.BackToMainMenu：两者都会让加载流程永久停住。
        public static void GoMainMenu() {
            Notes("正在返回主界面", "Returning to the main menu");
            // 点返回主界面即取消待加入：TickPendingJoin 每帧轮询，退出完成后会立刻 DoJoin。
            ClearPendingJoin("界面重置");
            _pendingSteamId = 0;   // 取消待加入，Steam 房号一并作废
            // reason 不能传自定义串：游戏会把它当成玩家可见提示弹出；传 null 则不覆盖游戏自己设的 abortReason。
            try { SRApi.LogInfo("[联机] 请求返回主界面（reason 传 null，避免把调试串弹给玩家）"); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            try { Matchmaker.ReturnToMainMenu(null); }
            catch (Exception e) { SRApi.LogWarn("返回主界面失败: " + e.Message); }
            // 不等游戏的优雅收尾，先做无损的部分：放出被等待的加载遮罩、清房间列表与表格几何缓存、收界面状态。
            // 动游戏状态机的收尾（Disconnect / 销毁 LobbyManager / 切场景）只交给看门狗。
            NudgeSplash();
            ClearLobbyList();
            try { SRApi.ForceResetUiState(); } catch (Exception __ex) { SRApi.Guard.Log("返回主界面：重置界面状态", __ex); }
            ArmWatchdog();
        }

        public static void DisbandOrLeave() {
            // 已在返回倒计时中：再点一次即强制立即返回，不等游戏自己的加载流程。
            if (_abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f) { ForceMainMenu(); return; }
            // 看门狗已经超时（三个 deadline 都归零）但还没回到主界面：第二次点击同样按"强制返回"处理，
            // 否则会再走一遍 GoMainMenu，把游戏自己的收尾流程又触发一次（audit B-1）。
            try {
                if (_returnAttemptedAt > 0f && SceneManager.GetActiveScene().name != "MainMenu") {
                    SRApi.LogWarn("[联机] 上次返回未完成，本次点击按强制返回处理");
                    ForceMainMenu();
                    return;
                }
            } catch (Exception __b1) { SRApi.Guard.Log("联机/返回状态检查", __b1); }
            try {
                LobbyManager lm = LobbyManager.instance;
                bool host = false;
                // 房主判定失败会退回房客分支（只退自己、不广播 HostEndedGame），故须留痕。
                try { host = lm != null && lm.IsHost; } catch (Exception __ex) { SRApi.Guard.Log("判定是否房主", __ex); }
                Notes(host ? "正在解散房间" : "正在退出房间", host ? "Disbanding the session" : "Leaving the session");
            } catch (Exception __ex) { SRApi.Guard.Log("退出联机提示", __ex); }
            // 房主：ReturnToMainMenu 先广播 HostEndedGame，全员一起回主界面；房客：同一条路只退自己。
            _returnAttemptedAt = Time.unscaledTime;
            GoMainMenu();
        }

        // 强制返回主界面，不等游戏自己的加载流程；每一步独立 try，任一步失败都继续往下。
        // 顺序：放出加载遮罩 -> 断开并销毁 LobbyManager -> 收界面状态 -> 温和加载主界面 -> 2 秒后硬切（见 TickWatchdog）。
        public static void ForceMainMenu() {
            // 已在主界面：不重复加载。
            if (SceneManager.GetActiveScene().name == "MainMenu") {
                _abortDeadline = 0f; _forceDeadline = 0f; _forceRawDeadline = 0f;
                Notes("已在主界面", "Already in the main menu");
                return;
            }
            Notes("正在强制返回主界面", "Force-returning to the main menu");
            ClearPendingJoin("强制返回主界面");   // 同时取消待加入，否则退出后仍会被拉进目标房
            _pendingSteamId = 0; // Steam 房号随之作废
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            SRApi.LogWarn("[联机] 强制返回主界面（手动）");
            NudgeSplash();
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    // 每一步独立 try、失败继续往下，但异常必须记录（Guard.Log 内部去重）。
                    try { lm.Disconnect("SR_UCH force"); } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：断开连接", __ex); }
                    try { UnityEngine.Object.Destroy(lm.gameObject); } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：销毁 LobbyManager", __ex); }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：收尾联机状态", __ex); }
            try { SRApi.ForceResetUiState(); } catch (Exception __ex) { SRApi.Guard.Log("强制返回主界面：重置界面状态", __ex); }
            try { SceneManagerWrapper.LoadScene("MainMenu"); }
            catch (Exception e) { SRApi.LogWarn("强制加载主界面失败: " + e.Message); }
            _forceRawDeadline = Time.unscaledTime + 2f;   //5) 二级兜底
        }

        private static void SetShow(UISplashScreen s) {
            try {
                if (ReferenceEquals(s, null)) return;             //已销毁的场景内遮罩也要改（协程读的是托管字段）
                if (s.State != UISplashScreen.STATE.SHOW) s.State = UISplashScreen.STATE.SHOW;
            } catch (Exception __ex) { SRApi.Guard.Log("放出加载遮罩", __ex); }
        }

        // 把被 FadeToLevel 等待的遮罩推到 SHOW，让卡住的协程继续往下走。
        private static void NudgeSplash() {
            try { SetShow(LoadingInterstitialSplash.Instance); } catch (Exception __ex) { SRApi.Guard.Log("放出加载遮罩(Instance)", __ex); }
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null && _fLsFadeOut != null) {
                    SetShow(_fLsFadeOut.GetValue(lm.CurrentLevelSelectController) as UISplashScreen);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("放出加载遮罩(关卡选择)", __ex); }
        }


        // 房间列表：取匹配器必须先用 IsInstantiated 询问是否已实例化。
        // Matchmaker.Instance 是惰性创建，未实例化时访问它会顺带 new 一个 SteamMatchmaker。
        private static Matchmaker MatchmakerOrNull() {
            try { return Matchmaker.IsInstantiated ? Matchmaker.Instance : null; }
            catch { return null; }
        }

        public static void RefreshLobbies() {
            if (!SRApi.GateMaster) return;   // 受 SR 总开关限制
            try {
                Matchmaker mm = MatchmakerOrNull();
                // 判空必须放最前：下面的 _pendingSteamId 分支会立刻调用 mm.GetType()。
                if (mm == null) {
                    _lobbyMsg = SRApi.T("联机未初始化：请先进入游戏的联机界面一次", "Online not initialised: open the game's online screen once first");
                    _lobbyErr = true;
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
                // 同一 sLobbyID 的回调可能重复到达：去重替换，否则会画出重复行、计数虚高、排序时行闪。
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
                // 只标记"变了"：排序与统计延迟到渲染时各做一次。
                _lobbiesDirty = true;
            } catch (Exception __ex) { SRApi.Guard.Log("房间列表回调", __ex); }
        }


        public static void TickOnline() {
            TickWatchdog();
            TickPendingJoin();
            TickJoinState();
        }

        // ---------- 加入房间后自动进入主菜单（跳过"按一次空格/确认"） ----------
        // 游戏在加入成功后由 Matchmaker.startGameOnInput() 停住等 WaitingForController：
        // 必须等主菜单里有 Player 才会继续加载树屋。这里用主控制器直接替玩家完成那一次确认。
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
                // Matchmaker.Instance 是惰性创建：玩家没开过联机界面就访问会顺带 new 一个（Steam 侧副作用）。
            // 统一走 MatchmakerOrNull()，未实例化时直接放弃。
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
                // 没有“主控制器”时用游戏自己的键盘控制器（原生按空格/确认用的就是它），
                // 否则从 SR 列表点加入（纯鼠标操作）时永远拿不到控制器，只能手动按键。
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


        // 游戏本地化小工具：用游戏自己的 I2 术语表，取不到则退回默认值。
        private static void RenderOnlinePage() {
            // 本页无页面级滚动：表头 / 筛选必须始终可见，房间只在下方框内滚动。
            SRApi.PageScroll = Vector2.zero;
            SRApi.NoPageScrollBars = true;   // 页面级横纵向滚动条都关闭
            // 列宽拖动 / 表格高度拖动的收尾都由核心处理，这里无需再收尾。
            EnsureWidths();
            // 页面内容在布局空间的起点 y（零高度探针），用于计算表格上方已用高度。
            Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
            if (Event.current != null && Event.current.type == EventType.Layout) _pageTopY = topProbe.y;

            // 按钮顺序：刷新列表 / 退出联机 / 房间码。
            GUILayout.BeginHorizontal();
            if (SRApi.Ctl.HotkeyButton("online.lobbies", SRApi.T("刷新列表", "Refresh"),
                SRApi.T("向匹配服务查一次公开房间, 能加入的排在表格最上面, 点里面的加入就行.\n如果你正待在某个房间里, 点加入会先退出当前房间, 退完立刻自动进新的.",
                     "Query the public lobby list, joinable lobbies on top. Click Join in the table.\nIf you are already in a lobby, clicking Join leaves it first and joins the new one right away."),
                RefreshLobbies, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(30))) RefreshLobbies();
            // 返回倒计时中按钮文字改为"强制返回"，再点一次即立刻返回。
            bool returning = _abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f;
            if (SRApi.Ctl.HotkeyButton("online.disband",
                returning ? SRApi.T("强制返回", "Force return") : SRApi.T("返回主界面", "Back to main menu"),
                SRApi.T("回主界面, 走的就是游戏自己的离开房间流程. 房主会先广播 HostEndedGame, 全员一起回去; 房客只退自己.\n点下去当帧就放出加载遮罩并清掉本页房间列表; 1.5 秒还没回去就自己收尾, 4 秒硬切主界面.\n再点一次这个按钮 = 立刻强制返回, 完全不等游戏自己的加载流程.",
                     "Back to the main menu, using the game's own leave-lobby flow. Host: broadcasts HostEndedGame and everyone returns; guest: only you leave.\nThe moment you click it releases the loading splash and clears this page's lobby list; self-recovery at 1.5s / 4s.\nClick it again to force an immediate return, skipping the game's own loading flow."),
                DisbandOrLeave, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(30))) DisbandOrLeave();
            // 房间码加入，走游戏自己的 Matchmaker.JoinLobby(code, useCode:true)。
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

            // 搜索结束的权威信号是 Matchmaker.Searching（游戏在最后一条房间回调之后才置 false），超时仅作兜底。
            // Searching 是实例属性，须先用 IsInstantiated 询问，避免读属性触发 .Instance 的惰性创建。
            if (_lobbySearching) {
                bool gameDone = false;
                try { if (Matchmaker.IsInstantiated) gameDone = !Matchmaker.Instance.Searching; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                if (gameDone || Time.unscaledTime - _lobbySearchAt > SearchTimeout()) {
                    _lobbySearching = false;

                        }
            }
            // 列表或可见性变化时重排并刷新统计，否则最多每 1 秒重排一次。
            if (_lobbiesDirty || Time.unscaledTime - _lobbySortAt >= 1f) {
                // 统计文案仅在列表真的变了或搜索已结束时刷新，避免把"搜索中..."覆盖成"共 0 个房间"。
                // 错误提示粘住：_lobbyErr 时不覆盖 _lobbyMsg。
                bool refreshMsg = !_lobbyErr && (_lobbiesDirty || !_lobbySearching);
                _lobbySortAt = Time.unscaledTime;
                if (refreshMsg) UpdateLobbyMsg();
            }
            string hint = SRApi.T("（点刷新列表查询）表头：左键点=按该列排序（升->降->默认），拖分隔线=调列宽（<->），拖表头=换列位置（+），Shift+点游戏质量=显示/隐藏匹配质量系数，右键=隐藏该列，Shift+右键=还原；滚轮=上下，Ctrl+滚轮=左右；表格底边可拖高度",
                               "(click 'Refresh' to query) Header: left-click = sort by that column (asc/desc/default), drag a separator = resize (<->), drag a header = reorder (+), Shift+click 'Quality' = show/hide the skill-match coefficient, right-click = hide that column, Shift+right-click = reset; wheel = up/down, Ctrl+wheel = left/right; drag the table's bottom edge to resize height");
            GUILayout.Label(string.IsNullOrEmpty(_lobbyMsg) ? hint : _lobbyMsg, SRApi.Ctl.Label);

            // 筛选：单行 区域 / 模式 / 标签 / 状态（状态下沉到标签右面）；下拉框宽度按 4 个均分。
            float cellW = Mathf.Max(SRApi.Ctl.Sc(96), (SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(40)) / 4f - SRApi.Ctl.Sc(8));
            GUILayout.Space(SRApi.Ctl.Sc(2));
            GUILayout.BeginHorizontal();
            DropFilter("区域", "Region", "按服务器区域筛选（亚太/欧盟/美国）。", "Filter by server region (AP / EU / US).", "Filter Region", RegionVals(), cellW);
            DropFilter("模式", "Mode", "按游戏模式筛选。", "Filter by game mode.", "Filter Mode", ModeVals, cellW);
            DropFilter("标签", "Tag", "按房间标签筛选。", "Filter by lobby tag.", "Filter Tag", TagVals, cellW);
            DropFilter("状态", "State", "按房间状态筛选：可加入 / 人满 / 对局中。", "Filter by lobby state: joinable / full / in match.", "Filter State", StateVals, cellW);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 表格自绘：表头固定，只有房间行滚动；滚动条自绘以跟随界面缩放并在拖到列表外时不断。
            GUILayout.Space(SRApi.Ctl.Sc(3));
            Rect last = GUILayoutUtility.GetLastRect();
            // 宽度用纯尺寸计算：布局矩形与屏幕矩形（ContentArea）不同坐标系，混算会导致表格框超宽、操作列被推出屏幕。
            float availW = Mathf.Max(SRApi.Ctl.Sc(220), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(12));
            // 高度：核心统一公式（页面视口 − 页面已用高度，再按 Table Height 夹取）。
            // 与预设列表共用核心高度：min(可用, 表头 + 行数*行高 + 拖拽条)。
            float availH = SRApi.Ctl.ListTableHeight(last.yMax, _pageTopY, _tableH, SRApi.Ctl.Sc(140),
                _shownLobbies != null ? _shownLobbies.Count : 0, SRApi.Ctl.TableRowH(), SRApi.Ctl.TableHeaderH(),
                SRApi.Ctl.TableGripH() + SRApi.Ctl.TableGripGap() * 2f, true);   // 底部贴页面底：始终填满
            // 高度由核心拖拽条直接写入配置，这里只按可用高度铺满。
            float boxH = availH;
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Width(availW), GUILayout.Height(boxH));

            TableDraw(box);
            TableInput(box, Event.current);

            HeightGrip();   // 表格底边：拖动改高度
        }

        // 表格底边拖动条；Shift+右键还原列宽 / 高度 / 列隐藏（房间行不再支持右键隐藏）。
        private static string DispName(string key, string v) {
            if (string.IsNullOrEmpty(v)) return SRApi.T("全部", "All");
            if (key == "Filter Region") return DispRegion(v);
            if (key == "Filter Mode") return SRApi.T(v == "OTHER" ? "其它" : v == "FREEPLAY" ? "自由" : v == "CREATIVE" ? "创意" : v == "PARTY" ? "派对" : "挑战",
                                                  v == "FREEPLAY" ? "Freeplay" : v == "CREATIVE" ? "Creative" : v == "PARTY" ? "Party" : "Challenge");
            if (key == "Filter Tag") return SRApi.T(v == "Fun" ? "好玩" : v == "Competitive" ? "竞技" : v == "Beginner" ? "新手" : "自定义关卡",
                                                 v == "Fun" ? "Fun" : v == "Competitive" ? "Competitive" : v == "Beginner" ? "Beginner" : "Custom levels");
            if (key == "Filter State") return v == "join" ? SRApi.T("可加入", "joinable") : v == "full" ? SRApi.T("人满", "full") : v == "playing" ? SRApi.T("对局中", "in match") : SRApi.T("全部", "all");
            return v; // 区域等：值本身就是显示名
        }

        // 下拉筛选控件
        // 标签与下拉框都用固定宽度：RestoreLabel 会按文字实际宽度收缩，导致两行下拉框左右不齐。
        private const float FilterLabelW = 46f;
        private const float FilterComboW = 104f;
        // 固定选项值用静态数组，避免每帧构造。
        private static readonly string[] StateVals = { "join", "full", "playing" };   //#4 状态筛选

        // 下拉选项按版本戳缓存：选项只与界面语言和值列表长度有关，变化时才重建。
        // 区域一项的动态内容由 RegionVals 自己的静态缓存负责（见 RegionLangStamp）。
        private static readonly Dictionary<string, string[]> _optCache = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, string> _optStamp = new Dictionary<string, string>();
        // 区域选项固定给出，不依赖已刷新到的房间数据；来源为 RelayConstants.AVAILABLE_REGIONS（游戏自己的区域筛选所用）。
        // 逐个过 RegionCodeOf 归并成三大区，认不出来的保留原名，与房间行的区域列同一套规则。
        private static string[] _regionValsCache;
        private static int _regionValsVer = -1;

        // 版本戳 = 语言 id + 语言版本 hash + AVAILABLE_REGIONS 规模。
        // 区域代码依赖本地化短名，且该静态 List 可能在本页首次渲染之后才被填好，故规模变化也要重建。
        private static int RegionLangStamp() {
            unchecked {
                int h = 17;
                string s = SRApi.CurrentLanguageId + "|" + SRApi.LangVer;
                for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
                int n = -1;
                try { if (RelayConstants.AVAILABLE_REGIONS != null) n = RelayConstants.AVAILABLE_REGIONS.Count; } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                return h * 31 + n;
            }
        }

        private static string[] RegionVals() {
            int ver = RegionLangStamp();
            if (_regionValsCache != null && _regionValsVer == ver) return _regionValsCache;
            string[] built;
            try {
                System.Collections.Generic.List<string> vs = new System.Collections.Generic.List<string>();
                System.Collections.IList regs = RelayConstants.AVAILABLE_REGIONS;
                if (regs != null) {
                    for (int i = 0; i < regs.Count; i++) {
                        object r = regs[i];
                        if (r == null) continue;
                        string code = RegionCodeOf(r);
                        if (string.IsNullOrEmpty(code)) continue;
                        if (!vs.Contains(code)) vs.Add(code);   //按 AVAILABLE_REGIONS 的原始次序去重
                    }
                }
                // 补上"无区域数据"这一档（放最后）：否则 UnityServerRegion 为 null 的房间在下拉里没有对应选项。
                if (!vs.Contains(NoneCode)) vs.Add(NoneCode);
                built = vs.Count > 0 ? vs.ToArray() : new[] { "AP", "EU", "US", NoneCode };
            } catch { built = new[] { "AP", "EU", "US" }; }
            _regionValsCache = built;
            _regionValsVer = ver;
            return built;
        }

        private static string[] FilterOptions(string key, string[] values) {
            // 语言戳必须带语言 id + 版本号：只用 "en"/"zh" 时从语言包 A 切到 B 戳不变，选项会停在旧译文。
            string stamp = SRApi.CurrentLanguageId + SRApi.LangVer + "|" + (values == null ? "-" : values.Length.ToString());
            string[] cached;
            string old;
            if (_optCache.TryGetValue(key, out cached) && _optStamp.TryGetValue(key, out old) && old == stamp) return cached;
            List<string> opts = new List<string>(1 + (values != null ? values.Length : 0));
            opts.Add(SRApi.T("全部", "All"));
            if (values != null) {
                for (int i = 0; i < values.Length; i++) opts.Add(DispName(key, values[i]));
            }
            cached = opts.ToArray();
            _optCache[key] = cached;
            _optStamp[key] = stamp;
            return cached;
        }

        // 与 FilterOptions 一一对应的"值"数组（vals[0]="全部"对应空串）。
        private static readonly Dictionary<string, string[]> _optValsCache = new Dictionary<string, string[]>();
        private static string[] FilterVals(string key, string[] values) {
            string stamp = SRApi.CurrentLanguageId + SRApi.LangVer + "|" + (values == null ? "-" : values.Length.ToString());
            string[] cached;
            string old;
            if (_optValsCache.TryGetValue(key, out cached) && _optStamp.TryGetValue(key, out old) && old == stamp) return cached;
            List<string> vs = new List<string>(1 + (values != null ? values.Length : 0));
            vs.Add("");
            if (values != null) for (int i = 0; i < values.Length; i++) vs.Add(values[i]);
            cached = vs.ToArray();
            _optValsCache[key] = cached;
            _optStamp[key] = stamp;
            return cached;
        }

        // 筛选行标签宽度：按当前语言最长的标签实测，兼顾不截断与下拉框左右对齐。
        private static float _filterLabelW = -1f;
        private static string _filterLabelLKey;
        private static float FilterLabelWidth() {
            string lk = SRApi.CurrentLanguageId + "|" + SRApi.LangVer;
            if (_filterLabelW >= 0f && _filterLabelLKey == lk) return _filterLabelW;
            float w = 0f;
            string[] ls = { SRApi.T("区域", "Region"), SRApi.T("模式", "Mode"), SRApi.T("标签", "Tag"), SRApi.T("人数", "Players"), SRApi.T("AFK", "AFK") };
            for (int i = 0; i < ls.Length; i++) w = Mathf.Max(w, SRApi.Ctl.Label.CalcSize(new GUIContent(ls[i])).x);
            _filterLabelW = Mathf.Max(SRApi.Ctl.Sc(FilterLabelW), w + SRApi.Ctl.Sc(8));
            _filterLabelLKey = lk;
            return _filterLabelW;
        }

        private static void DropFilter(string labelZh, string labelEn, string tipZh, string tipEn, string key, string[] values, float cellW) {
            try {
                // 本帧只查一次 entry，当前值与写回都复用它（见 FindStr / SetStr）。
                ConfigEntryBase e = SRApi.Ctl.FindEntry(SecOnline, key);
                float labelW = FilterLabelWidth();
                float comboW = SRApi.Ctl.Sc(FilterComboW);
                GUILayout.BeginHorizontal(GUILayout.Width(labelW + comboW + SRApi.Ctl.Sc(14)));
                GUILayout.Label(new GUIContent(SRApi.T(labelZh, labelEn), SRApi.T(tipZh, tipEn)), SRApi.Ctl.Label,
                    GUILayout.Width(labelW), GUILayout.Height(SRApi.Ctl.Sc(26)));
                // 选项走缓存（见 FilterOptions），不每帧新建 List；改值不带动页面滚动条。
                string[] opts = FilterOptions(key, values);
                string[] vals = FilterVals(key, values);   // 与 opts 一一对应，选中后按下标写回
                //同时只允许一个下拉框展开
                bool open = _openCombo == key;
                int dsel = SRApi.Ctl.ComboBox(e, DispName(key, FindStr(e)), vals, opts, ref open, comboW);
                if (dsel >= 0) {
                    SetStr(e, dsel == 0 ? "" : (values != null && dsel - 1 < values.Length ? values[dsel - 1] : ""));
                    _openCombo = "";
                    open = false;
                }
                if (open) {
                    if (_openCombo != key) _openCombo = key;
                } else if (_openCombo == key) {
                    _openCombo = "";
                }
                GUILayout.EndHorizontal();
            } catch (Exception __ex) { SRApi.Guard.Log("房间筛选下拉", __ex); }
        }

        // 按 key 查值的入口，内部转给按 entry 的重载。
        private static string FindStr(string key) {
            try { return FindStr(SRApi.Ctl.FindEntry(SecOnline, key)); }
            catch { return ""; }
        }

        // 直接从已查到的 entry 上读当前值，不再重复 FindEntry。
        private static string FindStr(ConfigEntryBase e) {
            try { return e != null ? (e.BoxedValue as string ?? "") : ""; }
            catch { return ""; }
        }

        // 接收已查好的 entry，不再自己 FindEntry。
        private static void SetStr(ConfigEntryBase e, string value) {
            try {
                if (e != null) {
                    SRApi.Ctl.SetValue(e, value);
                    // 筛选条件变化需立即标脏，否则"筛选后 N 个"要等 1 秒兜底才更新。
                    _lobbiesDirty = true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("设置房间筛选", __ex); }
        }

        // 列宽 / 列顺序 / 隐藏列（可拖动 + 持久化）
        // 用显式的 _widthsLoaded 作为一次性初始化哨兵。
        // 核心列状态：接管列宽/列序/隐藏列（格式与旧配置兼容）。
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
                    // 兼容 v5 之前的 12 项老值（第 4 项是已删除的技能系数列）：先裁掉它再按列数套用，
                    // 否则整串长度对不上会被丢弃（用户调好的列宽白丢）。
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
                        // 人数 / 综合分：默认宽已 +20%，旧持久化值更窄时按新默认抬高，避免表头被截。
                        if ((i == 1 || i == 9) && _cw[i] < DefWidths[i]) _cw[i] = DefWidths[i];
                    }
                        }
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("读取联机表格列宽", __ex); } // 一次性：读不到就用默认列宽，留痕
            EnsureColOrder();
            EnsureHiddenCols();
        }

        // 列顺序必须是 0..ColCount-1 的一个排列，否则忽略（防手改配置把表格搞崩）。
        // "Column Order" 没有版本号：老值是 12 项（含已删除的技能系数列 3），先去掉 3 并把其后的索引统一 -1，
        // 再按新列数校验；裁剪后仍不合法就整串忽略（回默认顺序，不崩也不藏错列）。
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

        // v5 起配置键已换（值就是当前列索引），这里仍逐项做越界裁剪，防手改配置把不存在的列加进隐藏集合。
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

        // 表格绘制（固定表头 + 细线分隔 + 列可拖动换位/隐藏/调宽）
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

        // 列顺序 / 被隐藏的列
        private static SRApi.TableState _cols;   // 列宽/列序/隐藏列（核心统一持久化）
        private static int[] _order;
        private static readonly HashSet<int> _hiddenCols = new HashSet<int>();
        private static ConfigEntry<string> _colOrder, _hiddenColsEntry;
        // 行裁剪框 / 表头裁剪框 / 滚动条矩形
        private static Rect _rowsClipPage, _joinHeaderPage;

        // 可见列（order + hidden -> 显示顺序）现由核心 TableDraw 自行推导，本页不再维护副本。

        // 单击一帧只处理一次，避免多条命中路径同时命中导致来回切换。
        private static void HeaderClickOnce(int ci) {
            if (_clickFrame == Time.frameCount) return;
            _clickFrame = Time.frameCount;
            HeaderClick(ci);
        }

    }
}
